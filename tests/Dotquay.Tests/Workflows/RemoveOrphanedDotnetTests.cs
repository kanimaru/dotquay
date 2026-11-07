using Dotquay.Core.Dotnet;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <c>dotquay remove</c> on a NuGet package that only the lockfile still mentions
/// (DESIGN-v0.19 §4).
/// </summary>
/// <remarks>
/// Before v0.19, doctor reported such an entry and told the user to run <c>dotquay remove X</c>,
/// and that command then failed with "not declared in dotquay.toml". The graph view offers the same
/// Remove on an orphan package node, so both routes depend on this.
/// </remarks>
public class RemoveOrphanedDotnetTests {
    private const string ProjectDirectory = "project";
    private const string RootUrl = "https://example.com/root.git";

    private static string Sha(char c) => new(c, 40);

    private const string Csproj = """
        <Project Sdk="Godot.NET.Sdk/4.3.0">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Ghost.Package" Version="2.0.0" />
            <PackageReference Include="Serilog" Version="3.1.1" />
          </ItemGroup>
        </Project>
        """;

    private static readonly string ProjectManifest = $"""
        [project]
        godot = "4.7"

        [addons.root]
        git = "{RootUrl}"
        version = "~1.0"
        """;

    private static readonly string Lockfile = $"""
        [[addon]]
        name = "root"
        git = "{RootUrl}"
        resolved = "{Sha('a')}"
        version = "v1.0.0"
        path = "addons/root"

        [[dotnet]]
        name = "Ghost.Package"
        resolved = "2.0.0"

        [[dotnet]]
        name = "Serilog"
        resolved = "3.1.1"
        """;

    private const string RootDeclaresSerilog = """
        [package]
        name = "root"
        version = "1.0.0"

        [dotnet.Serilog]
        version = "3.*"
        """;

    private static (DependencyWorkflow Workflow, InMemoryFileSystem FileSystem) Build() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), ProjectManifest);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), Lockfile);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "Game.csproj"), Csproj);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "addons", "root", "plugin.cfg"), "cfg");
        fileSystem.WriteAllText(
            fileSystem.CombinePath(ProjectDirectory, "addons", "root", "dotquay.toml"), RootDeclaresSerilog);

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        var dotnetInstaller = new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None());
        var workflow = new DependencyWorkflow(
            fileSystem, gitClient, new FakeNuGetClientFactory(new FakeNuGetClient()), addonInstaller, dotnetInstaller);

        return (workflow, fileSystem);
    }

    [Fact]
    public async Task RemoveAsync_OrphanPackage_DropsTheLockEntryAndThePackageReference() {
        var (workflow, fileSystem) = Build();

        var removal = await workflow.RemoveAsync(ProjectDirectory, "Ghost.Package");

        // Reported as what it was, so `remove --json` says "dotnet" rather than "addon".
        Assert.Equal(DependencyKind.Dotnet, removal.Kind);

        var lockfile = LockfileReader.Read(fileSystem.ReadAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock")));
        Assert.DoesNotContain(lockfile.Dotnet, d => d.Name == "Ghost.Package");
        Assert.Contains(lockfile.Dotnet, d => d.Name == "Serilog");

        var csproj = fileSystem.ReadAllText(fileSystem.CombinePath(ProjectDirectory, "Game.csproj"));
        Assert.DoesNotContain("Ghost.Package", csproj);
        Assert.Contains("Serilog", csproj);
    }

    [Fact]
    public async Task RemoveAsync_OrphanPackage_LeavesTheManifestAlone() {
        var (workflow, fileSystem) = Build();
        var manifestPath = fileSystem.CombinePath(ProjectDirectory, "dotquay.toml");

        await workflow.RemoveAsync(ProjectDirectory, "Ghost.Package");

        // It was never in the manifest, so the manifest has nothing to lose.
        Assert.Equal(ProjectManifest, fileSystem.ReadAllText(manifestPath));
    }

    [Fact]
    public async Task RemoveAsync_PackageAReachableAddonDeclares_IsRefusedAndNamesTheAddon() {
        var (workflow, fileSystem) = Build();
        var lockPath = fileSystem.CombinePath(ProjectDirectory, "dotquay.lock");

        var exception = await Assert.ThrowsAsync<WorkflowException>(() => workflow.RemoveAsync(ProjectDirectory, "Serilog"));

        // The next update would union it straight back in (requirement P1-5). Refusing, and saying
        // who to talk to instead, beats a removal that silently undoes itself.
        Assert.Contains("'root'", exception.Message);
        Assert.Equal(Lockfile, fileSystem.ReadAllText(lockPath));
    }

    [Fact]
    public async Task RemoveAsync_NameNeitherDeclaredNorLocked_StillSaysNotDeclared() {
        var (workflow, _) = Build();

        var exception = await Assert.ThrowsAsync<WorkflowException>(() => workflow.RemoveAsync(ProjectDirectory, "Nope"));

        Assert.Contains("is not declared in", exception.Message);
    }
}
