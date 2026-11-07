using Dotquay.Core.Dotnet;
using Dotquay.Core.Dotnet.SourceBuild;
using Dotquay.Core.Json;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet.SourceBuild;

/// <summary>
/// DESIGN-v0.26 §B: a package the project builds from source gets no PackageReference from any command, an
/// existing one is removed (decision 1), and the lockfile, feeds and restore behave exactly as before.
/// </summary>
public class SourceBuiltInstallTests {
    private const string ProjectDir = "project";
    private const string CsprojPath = "project/Game.csproj";
    private const string Package = "Acme.Core";
    private const string Include = @"..\src\Acme.Core\Acme.Core.csproj";

    private const string Library = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <PackageId>Acme.Core</PackageId>
            <Version>0.7.50</Version>
          </PropertyGroup>
        </Project>
        """;

    private static string Consumer(string extraItems = "", string referenceAttributes = "") => $"""
        <Project Sdk="Godot.NET.Sdk/4.5.0">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="{Include}"{referenceAttributes} />
          </ItemGroup>
          {extraItems}
        </Project>
        """;

    private static string Duplicate => $"""<ItemGroup><PackageReference Include="{Package}" Version="0.7.20" /></ItemGroup>""";

    // --- the installer -------------------------------------------------------------------------------

    private static (DotnetInstaller Installer, InMemoryFileSystem FileSystem, FakeDotnetCliRunner Runner) BuildInstaller(
        string csproj, bool withLibrary = true) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(CsprojPath, csproj);
        if (withLibrary) {
            fileSystem.WriteAllText("src/Acme.Core/Acme.Core.csproj", Library);
        }

        var runner = new FakeDotnetCliRunner();
        return (new DotnetInstaller(fileSystem, runner, TestCredentials.None()), fileSystem, runner);
    }

    [Fact]
    public async Task Installer_SourceBuiltPackage_WritesNothing_ButStillRestoresAndLocks() {
        var (installer, fileSystem, runner) = BuildInstaller(Consumer());
        var before = fileSystem.ReadAllText(CsprojPath);

        var outcome = await installer.InstallWithOutcomeAsync(ProjectDir, Package, "0.7.20");

        Assert.Equal(before, fileSystem.ReadAllText(CsprojPath));
        Assert.Equal("0.7.20", outcome.Locked.Resolved);
        Assert.NotNull(outcome.SourceBuilt);
        Assert.False(outcome.SourceBuilt.RemovedPackageReference);
        Assert.Equal(Include, outcome.SourceBuilt.Provider.Include);
        Assert.Single(runner.RestoreCalls);
    }

    [Fact]
    public async Task Installer_SourceBuiltPackage_RemovesAnExistingDuplicate_AndKeepsTheProjectReference() {
        var (installer, fileSystem, _) = BuildInstaller(Consumer(Duplicate));

        var outcome = await installer.InstallWithOutcomeAsync(ProjectDir, Package, "0.7.20", restore: false);

        var csproj = fileSystem.ReadAllText(CsprojPath);
        Assert.DoesNotContain("PackageReference", csproj);
        Assert.Contains($"ProjectReference Include=\"{Include}\"", csproj);
        Assert.True(outcome.SourceBuilt?.RemovedPackageReference);
    }

    [Fact]
    public async Task Installer_PlainInstallAsync_StillReturnsTheSameLockEntry() {
        var (installer, _, _) = BuildInstaller(Consumer());

        var locked = await installer.InstallAsync(ProjectDir, Package, "0.7.20", restore: false);

        Assert.Equal(Package, locked.Name);
        Assert.Equal("0.7.20", locked.Resolved);
    }

    [Fact]
    public async Task Installer_ConditionalReference_StillWritesThePackageReference() {
        var (installer, fileSystem, _) = BuildInstaller(Consumer(referenceAttributes: " Condition=\"'$(Configuration)' == 'Debug'\""));

        var outcome = await installer.InstallWithOutcomeAsync(ProjectDir, Package, "0.7.20", restore: false);

        Assert.Null(outcome.SourceBuilt);
        Assert.Contains($"PackageReference Include=\"{Package}\" Version=\"0.7.20\"", fileSystem.ReadAllText(CsprojPath));
    }

    [Fact]
    public async Task Installer_ReferencedProjectMissing_StillWritesThePackageReference() {
        var (installer, fileSystem, _) = BuildInstaller(Consumer(), withLibrary: false);

        var outcome = await installer.InstallWithOutcomeAsync(ProjectDir, Package, "0.7.20", restore: false);

        Assert.Null(outcome.SourceBuilt);
        Assert.Contains($"PackageReference Include=\"{Package}\"", fileSystem.ReadAllText(CsprojPath));
    }

    [Fact]
    public async Task Installer_OtherPackages_AreUnaffected() {
        var (installer, fileSystem, _) = BuildInstaller(Consumer());

        var outcome = await installer.InstallWithOutcomeAsync(ProjectDir, "Newtonsoft.Json", "13.0.3", restore: false);

        Assert.Null(outcome.SourceBuilt);
        Assert.Contains("PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\"", fileSystem.ReadAllText(CsprojPath));
    }

    [Fact]
    public void Describe_NamesTheReferenceAndTheSourceVersion_InBothForms() {
        var provider = new SourceReference(Include, "src/Acme.Core/Acme.Core.csproj", null, new ProjectIdentity(Package, "0.7.50"));

        Assert.Equal(
            $"Acme.Core: built from source by {Include} (0.7.50), so no PackageReference was written.",
            new SourceBuiltPackage(Package, provider, RemovedPackageReference: false).Describe());
        Assert.Equal(
            $"Acme.Core: built from source by {Include} (0.7.50), so its PackageReference was removed from the .csproj - it could shadow the source build.",
            new SourceBuiltPackage(Package, provider, RemovedPackageReference: true).Describe());
    }

    // --- through the workflow ------------------------------------------------------------------------

    private static (DependencyWorkflow Workflow, InMemoryFileSystem FileSystem, FakeNuGetClient NuGet) BuildWorkflow(string csproj) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\n");
        fileSystem.WriteAllText(CsprojPath, csproj);
        fileSystem.WriteAllText("src/Acme.Core/Acme.Core.csproj", Library);

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        var nuGet = new FakeNuGetClient();
        nuGet.VersionsByPackageId[Package] = ["0.7.20"];

        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        var dotnetInstaller = new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None());
        var workflow = new DependencyWorkflow(fileSystem, gitClient, new FakeNuGetClientFactory(nuGet), addonInstaller, dotnetInstaller);

        return (workflow, fileSystem, nuGet);
    }

    [Fact]
    public async Task Add_SourceBuiltPackage_IsDeclaredAndLocked_ButNotReferenced() {
        var (workflow, fileSystem, _) = BuildWorkflow(Consumer());
        var before = fileSystem.ReadAllText(CsprojPath);

        var declaration = await workflow.DeclareDotnetAsync(ProjectDir, Package, "0.7.20");

        Assert.Equal(before, fileSystem.ReadAllText(CsprojPath));
        Assert.Equal("0.7.20", LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock")).Dotnet.Single().Resolved);
        Assert.Contains(Package, ManifestReader.ReadProject(fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml")).Dotnet.Keys);
        Assert.Contains(declaration.Messages(null), m => m.Contains("built from source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_Twice_LeavesTheCsprojByteIdentical_AndSaysWhyEachTime() {
        var (workflow, fileSystem, _) = BuildWorkflow(Consumer());
        await workflow.DeclareDotnetAsync(ProjectDir, Package, "0.7.20");
        var before = fileSystem.ReadAllText(CsprojPath);

        var first = await workflow.InstallAsync(ProjectDir, force: false);
        var second = await workflow.InstallAsync(ProjectDir, force: false);

        Assert.Equal(before, fileSystem.ReadAllText(CsprojPath));
        Assert.Single(first.SourceBuildNotes);
        Assert.Equal(first.SourceBuildNotes, second.SourceBuildNotes);
        Assert.Contains(first.Dotnet, d => d.Name == Package);
    }

    [Fact]
    public async Task Install_RepairsADuplicateOnce_ThenReportsNothingRemoved() {
        // vbeami's case: the PackageReference an older dotquay re-added beside the ProjectReference.
        var (workflow, fileSystem, _) = BuildWorkflow(Consumer());
        await workflow.DeclareDotnetAsync(ProjectDir, Package, "0.7.20");
        fileSystem.WriteAllText(CsprojPath, Consumer(Duplicate));

        var repaired = await workflow.InstallAsync(ProjectDir, force: false);
        var after = await workflow.InstallAsync(ProjectDir, force: false);

        Assert.DoesNotContain("PackageReference", fileSystem.ReadAllText(CsprojPath));
        Assert.Contains("was removed", Assert.Single(repaired.SourceBuildNotes));
        Assert.Contains("no PackageReference was written", Assert.Single(after.SourceBuildNotes));
    }

    [Fact]
    public async Task Install_NotesReachTheJsonMessages_TheFieldThePluginShows() {
        var (workflow, _, _) = BuildWorkflow(Consumer());
        await workflow.DeclareDotnetAsync(ProjectDir, Package, "0.7.20");

        var result = await workflow.InstallAsync(ProjectDir, force: false);

        Assert.Single(result.SourceBuildNotes);
        Assert.Equal(result.SourceBuildNotes, JsonMapper.ToJson(result).Messages);
    }

    [Fact]
    public async Task Update_WithAVersionChange_ReportsTheSourceBuild_AndWritesNoReference() {
        var (workflow, fileSystem, nuGet) = BuildWorkflow(Consumer());
        await workflow.DeclareDotnetAsync(ProjectDir, Package, "^0.7.20");
        nuGet.VersionsByPackageId[Package] = ["0.7.20", "0.7.21"];

        var result = await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        Assert.Equal("0.7.21", result.Dotnet.Single(d => d.Name == Package).Locked.Resolved);
        Assert.Single(result.SourceBuildNotes);
        Assert.Equal(result.SourceBuildNotes, JsonMapper.ToJson(result).Messages);
        Assert.DoesNotContain("PackageReference", fileSystem.ReadAllText(CsprojPath));
    }

    [Fact]
    public async Task Update_WithNothingNew_LeavesTheCsprojAlone_AndHasNothingToReport() {
        var (workflow, fileSystem, _) = BuildWorkflow(Consumer());
        await workflow.DeclareDotnetAsync(ProjectDir, Package, "0.7.20");
        var before = fileSystem.ReadAllText(CsprojPath);

        var result = await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        Assert.Equal(before, fileSystem.ReadAllText(CsprojPath));
        Assert.Empty(result.SourceBuildNotes);
    }

    [Fact]
    public async Task Remove_SourceBuiltPackage_NeverTouchesTheProjectReference() {
        var (workflow, fileSystem, _) = BuildWorkflow(Consumer());
        await workflow.DeclareDotnetAsync(ProjectDir, Package, "0.7.20");

        await workflow.RemoveAsync(ProjectDir, Package);

        Assert.Contains($"ProjectReference Include=\"{Include}\"", fileSystem.ReadAllText(CsprojPath));
        Assert.Empty(LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock")).Dotnet);
    }
}
