using Dotquay.Core.Dotnet;
using Dotquay.Core.Json;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <see cref="DependencyWorkflow.DeclareDotnetAsync"/>: <c>add &lt;id&gt; [--version]</c>
/// for a package the project doesn't know, has only locked, or already declares
/// (DESIGN-v0.19 §7a.1), plus the matching <c>remove</c> of a pin (§7a.2).
/// </summary>
/// <remarks>
/// The fixture is the Phase 1 verification project in miniature. It has one reachable addon, <c>root</c>,
/// whose installed package manifest declares <c>VBeami.Core</c>. The project itself declares
/// <c>Newtonsoft.Json</c>, pinned to the <c>core</c> feed with private assets, so a change can prove it
/// kept both.
/// </remarks>
public class DeclareDotnetTests
{
    private const string ProjectDirectory = "project";
    private const string RootUrl = "https://example.com/root.git";

    private static string Sha(char c) => new(c, 40);

    private static readonly string Manifest = $"""
        [project]
        godot = "4.7"

        [nuget.sources.core]
        url = "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json"

        [addons.root]
        git = "{RootUrl}"
        version = "~1.0"

        [dotnet.Newtonsoft.Json]
        version = "13.0.3"
        source = "core"
        private_assets = ["all"]
        """;

    private static readonly string Lockfile = $"""
        [[addon]]
        name = "root"
        git = "{RootUrl}"
        resolved = "{Sha('a')}"
        version = "v1.0.0"
        path = "addons/root"

        [[dotnet]]
        name = "Newtonsoft.Json"
        resolved = "13.0.3"
        source = "core"
        private_assets = ["all"]

        [[dotnet]]
        name = "VBeami.Core"
        resolved = "0.4.7"
        private_assets = ["analyzers"]

        [[dotnet]]
        name = "Zed.Last"
        resolved = "1.0.0"
        """;

    private const string RootPackageManifest = """
        [package]
        name = "root"
        version = "1.0.0"

        [dotnet."VBeami.Core"]
        version = "0.4.*"
        """;

    private const string Csproj = """
        <Project Sdk="Godot.NET.Sdk/4.7.0">
          <ItemGroup>
            <PackageReference Include="Newtonsoft.Json" Version="13.0.3" PrivateAssets="all" />
            <PackageReference Include="VBeami.Core" Version="0.4.7" PrivateAssets="analyzers" />
            <PackageReference Include="Zed.Last" Version="1.0.0" />
          </ItemGroup>
        </Project>
        """;

    private sealed record Fixture(
        DependencyWorkflow Workflow, InMemoryFileSystem FileSystem, FakeNuGetClientFactory Factory, FakeDotnetCliRunner Runner)
    {
        public ProjectManifest ReadManifest() =>
            ManifestReader.ReadProject(FileSystem.ReadAllText(FileSystem.CombinePath(ProjectDirectory, "dotquay.toml")));

        public DotquayLockfile ReadLockfile() =>
            LockfileReader.Read(FileSystem.ReadAllText(FileSystem.CombinePath(ProjectDirectory, "dotquay.lock")));

        public string ReadCsproj() => FileSystem.ReadAllText(FileSystem.CombinePath(ProjectDirectory, "Game.csproj"));
    }

    private static Fixture Build()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), Manifest);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), Lockfile);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "Game.csproj"), Csproj);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "addons", "root", "plugin.cfg"), "cfg");
        fileSystem.WriteAllText(
            fileSystem.CombinePath(ProjectDirectory, "addons", "root", "dotquay.toml"), RootPackageManifest);

        var publicFeed = new FakeNuGetClient()
            .With("VBeami.Core", "0.4.7", "0.4.9", "0.5.0")
            .With("Brand.New", "1.0.0", "2.0.0")
            .With("Zed.Last", "1.0.0", "1.1.0");
        var factory = new FakeNuGetClientFactory(publicFeed);
        factory.ClientsBySource["core"] = new FakeNuGetClient("core").With("Newtonsoft.Json", "12.0.3", "13.0.3");

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        var runner = new FakeDotnetCliRunner();
        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        var workflow = new DependencyWorkflow(
            fileSystem, gitClient, factory, addonInstaller, new DotnetInstaller(fileSystem, runner, TestCredentials.None()));

        return new Fixture(workflow, fileSystem, factory, runner);
    }

    [Fact]
    public async Task UnknownPackage_IsAdded()
    {
        var fixture = Build();

        var declaration = await fixture.Workflow.DeclareDotnetAsync(ProjectDirectory, "Brand.New");

        Assert.Equal(DotnetDeclarationOutcome.Added, declaration.Outcome);
        Assert.Equal("2.0.0", declaration.Locked.Resolved);
        Assert.Null(declaration.PreviousResolved);
        Assert.Equal("2.0.0", fixture.ReadManifest().Dotnet["Brand.New"].Version);
    }

    [Fact]
    public async Task DeclaredPackage_WithoutAVersion_IsRefusedAndNamesBothWaysForward()
    {
        var fixture = Build();

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => fixture.Workflow.DeclareDotnetAsync(ProjectDirectory, "Newtonsoft.Json"));

        Assert.Contains("dotquay update Newtonsoft.Json", exception.Message);
        Assert.Contains("--version", exception.Message);
    }

    [Fact]
    public async Task DeclaredPackage_WithANewVersion_IsChangedEverywhere()
    {
        var fixture = Build();

        var declaration = await fixture.Workflow.DeclareDotnetAsync(ProjectDirectory, "Newtonsoft.Json", "12.0.3");

        Assert.Equal(DotnetDeclarationOutcome.Changed, declaration.Outcome);
        Assert.Equal("13.0.3", declaration.PreviousResolved);
        Assert.Equal("12.0.3", fixture.ReadManifest().Dotnet["Newtonsoft.Json"].Version);
        Assert.Equal("12.0.3", fixture.ReadLockfile().Dotnet.Single(d => d.Name == "Newtonsoft.Json").Resolved);
        Assert.Contains("Include=\"Newtonsoft.Json\" Version=\"12.0.3\"", fixture.ReadCsproj());
        Assert.Single(fixture.Runner.RestoreCalls);
    }

    [Fact]
    public async Task DeclaredPackage_Change_KeepsTheFeedPinAndAssetMetadata()
    {
        var fixture = Build();

        await fixture.Workflow.DeclareDotnetAsync(ProjectDirectory, "Newtonsoft.Json", "12.0.3");

        // Only the version was a new statement. Losing the pin would let a public feed answer next
        // time; losing private_assets would leak the package to consumers.
        var spec = fixture.ReadManifest().Dotnet["Newtonsoft.Json"];
        Assert.Equal("core", spec.Source);
        Assert.Equal(["all"], spec.Assets.Private);
        Assert.Contains("core", fixture.Factory.RequestedPins);
    }

    [Fact]
    public async Task DeclaredPackage_SameVersion_IsUnchangedAndTouchesNothing()
    {
        var fixture = Build();
        var manifestBefore = fixture.FileSystem.ReadAllText(fixture.FileSystem.CombinePath(ProjectDirectory, "dotquay.toml"));

        var declaration = await fixture.Workflow.DeclareDotnetAsync(ProjectDirectory, "Newtonsoft.Json", "13.0.3");

        Assert.Equal(DotnetDeclarationOutcome.Unchanged, declaration.Outcome);
        Assert.Empty(fixture.Runner.RestoreCalls);
        Assert.Empty(fixture.Factory.RequestedPins);
        Assert.Equal(manifestBefore, fixture.FileSystem.ReadAllText(fixture.FileSystem.CombinePath(ProjectDirectory, "dotquay.toml")));
    }

    [Fact]
    public async Task AddonDeclaredPackage_WithAVersion_IsPinnedInTheProject()
    {
        var fixture = Build();

        var declaration = await fixture.Workflow.DeclareDotnetAsync(ProjectDirectory, "VBeami.Core", "0.4.9");

        Assert.Equal(DotnetDeclarationOutcome.Pinned, declaration.Outcome);
        Assert.Equal("0.4.7", declaration.PreviousResolved);
        Assert.Equal("0.4.9", fixture.ReadManifest().Dotnet["VBeami.Core"].Version);
        Assert.Contains("Include=\"VBeami.Core\" Version=\"0.4.9\"", fixture.ReadCsproj());
    }

    [Fact]
    public async Task AddonDeclaredPackage_Pin_InheritsTheAssetsDotquayAlreadyWrote()
    {
        var fixture = Build();

        var declaration = await fixture.Workflow.DeclareDotnetAsync(ProjectDirectory, "VBeami.Core", "0.4.9");

        // The addon's analyzer stayed private before the pin; declaring it in the project must not
        // quietly change that.
        Assert.Equal(["analyzers"], fixture.ReadManifest().Dotnet["VBeami.Core"].Assets.Private);
        Assert.Equal(["analyzers"], declaration.Locked.Assets.Private);
    }

    [Fact]
    public async Task AddonDeclaredPackage_Pin_KeepsTheLockfileOrder()
    {
        var fixture = Build();

        await fixture.Workflow.DeclareDotnetAsync(ProjectDirectory, "VBeami.Core", "0.4.9");

        Assert.Equal(["Newtonsoft.Json", "VBeami.Core", "Zed.Last"], fixture.ReadLockfile().Dotnet.Select(d => d.Name).ToList());
    }

    [Fact]
    public async Task LockedButUndeclared_WithoutAVersion_IsPinnedToTheLatestStable()
    {
        var fixture = Build();

        var declaration = await fixture.Workflow.DeclareDotnetAsync(ProjectDirectory, "Zed.Last");

        Assert.Equal(DotnetDeclarationOutcome.Pinned, declaration.Outcome);
        Assert.Equal("1.1.0", declaration.Locked.Resolved);
        Assert.Equal("1.0.0", declaration.PreviousResolved);
    }

    [Fact]
    public async Task VersionThatDoesNotExist_IsRefusedAndWritesNothing()
    {
        var fixture = Build();
        var lockBefore = fixture.FileSystem.ReadAllText(fixture.FileSystem.CombinePath(ProjectDirectory, "dotquay.lock"));

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => fixture.Workflow.DeclareDotnetAsync(ProjectDirectory, "VBeami.Core", "9.9.9"));

        Assert.Contains("9.9.9", exception.Message);
        Assert.Equal(lockBefore, fixture.FileSystem.ReadAllText(fixture.FileSystem.CombinePath(ProjectDirectory, "dotquay.lock")));
        Assert.DoesNotContain("VBeami.Core", fixture.ReadManifest().Dotnet.Keys);
    }

    [Fact]
    public async Task UnknownSource_IsRefusedBeforeAnyFeedIsAsked()
    {
        var fixture = Build();

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => fixture.Workflow.DeclareDotnetAsync(ProjectDirectory, "VBeami.Core", "0.4.9", pinnedSourceName: "nope"));

        Assert.Contains("--source 'nope'", exception.Message);
        Assert.Empty(fixture.Factory.RequestedPins);
    }

    [Fact]
    public async Task RemovingAPin_KeepsWhatTheAddonStillNeeds()
    {
        var fixture = Build();
        await fixture.Workflow.DeclareDotnetAsync(ProjectDirectory, "VBeami.Core", "0.4.9");

        var removal = await fixture.Workflow.RemoveAsync(ProjectDirectory, "VBeami.Core");

        // Undoing the pin hands the choice back to the addon. It must not pull the package out from
        // under the addon until the next update resolves the addon's range (DESIGN-v0.19 §7a.2).
        Assert.Equal(DependencyKind.Dotnet, removal.Kind);
        Assert.Equal(["root"], removal.StillRequiredBy);
        Assert.DoesNotContain("VBeami.Core", fixture.ReadManifest().Dotnet.Keys);
        Assert.Contains(fixture.ReadLockfile().Dotnet, d => d.Name == "VBeami.Core");
        Assert.Contains("Include=\"VBeami.Core\"", fixture.ReadCsproj());
        Assert.Contains("'root' still needs it", removal.Describe("VBeami.Core"));
    }

    [Fact]
    public async Task RemovingAPackageNoAddonNeeds_StillRemovesItCompletely()
    {
        var fixture = Build();

        var removal = await fixture.Workflow.RemoveAsync(ProjectDirectory, "Newtonsoft.Json");

        Assert.Empty(removal.StillRequiredBy);
        Assert.DoesNotContain(fixture.ReadLockfile().Dotnet, d => d.Name == "Newtonsoft.Json");
        Assert.DoesNotContain("Newtonsoft.Json", fixture.ReadCsproj());
        Assert.Equal("Removed 'Newtonsoft.Json'.", removal.Describe("Newtonsoft.Json"));
    }

    [Theory]
    [InlineData(DotnetDeclarationOutcome.Added, "added", null)]
    [InlineData(DotnetDeclarationOutcome.Pinned, "updated", "0.4.7")]
    [InlineData(DotnetDeclarationOutcome.Changed, "updated", "0.4.7")]
    [InlineData(DotnetDeclarationOutcome.Unchanged, "unchanged", "0.4.7")]
    public void ToChange_MapsEachOutcomeOntoTheWire(DotnetDeclarationOutcome outcome, string action, string? from)
    {
        var declaration = new DotnetDeclaration(
            new LockedDotnet { Name = "VBeami.Core", Resolved = "0.4.9" }, outcome, PreviousResolved: "0.4.7");

        var change = JsonMapper.ToChange(declaration);

        Assert.Equal("dotnet", change.Kind);
        Assert.Equal(action, change.Action);
        Assert.Equal(from, change.From);
        Assert.Equal("0.4.9", change.To);
    }

    [Fact]
    public void Describe_APin_SaysHowToUndoIt()
    {
        var declaration = new DotnetDeclaration(
            new LockedDotnet { Name = "VBeami.Core", Resolved = "0.4.9" }, DotnetDeclarationOutcome.Pinned, "0.4.7");

        var text = declaration.Describe(pinnedSourceName: null);

        Assert.Contains("0.4.7", text);
        Assert.Contains("remove it", text);
    }
}
