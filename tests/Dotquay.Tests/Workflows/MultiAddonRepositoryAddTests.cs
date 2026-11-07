using Dotquay.Core.Manifest;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// <c>dotquay add</c> against one repository that ships several addons — the BotV2 report:
/// <c>add …/vbeami/ --branch main --path godot/addons/vbeami_vcam --install-as vbeami_vcam</c> was
/// refused with "'vbeami' is already declared" because the name came from the URL alone (see
/// <see cref="AddonNaming"/>).
/// </summary>
public class MultiAddonRepositoryAddTests {
    private const string RepoUrl = "https://gitlab.kemomi.de/wild-alliance/vbeami/";
    private const string ProjectDir = "project";

    private static (DependencyWorkflow Workflow, InMemoryFileSystem FileSystem) Build() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\n");

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[RepoUrl] = new Dictionary<string, string> {
            ["godot/addons/vbeami/plugin.cfg"] = "core",
            ["godot/addons/vbeami_vcam/plugin.cfg"] = "vcam",
            ["godot/addons/vbeami_gdmp/plugin.cfg"] = "gdmp",
        };
        gitClient.BranchTips[(RepoUrl, "main")] = new string('b', 40);

        return (new DependencyWorkflow(fileSystem, gitClient, "cache"), fileSystem);
    }

    private static ProjectManifest ReadManifest(InMemoryFileSystem fileSystem) =>
        ManifestReader.ReadProject(fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"));

    private static Task<Core.Lockfile.LockedAddon> AddCore(DependencyWorkflow workflow) =>
        workflow.AddAsync(ProjectDir, RepoUrl,
            new AddonAddOptions { Branch = "main", Path = "godot/addons/vbeami" });

    [Fact]
    public async Task AddAsync_SecondSubtreeWithInstallAs_IsAddedUnderItsInstallAsName() {
        var (workflow, fileSystem) = Build();
        await AddCore(workflow);

        // Exactly the reported command line.
        var locked = await workflow.AddAsync(
            ProjectDir,
            RepoUrl,
            new AddonAddOptions {
                Branch = "main",
                Path = "godot/addons/vbeami_vcam",
                InstallAs = "vbeami_vcam"
            });

        Assert.Equal("vbeami_vcam", locked.Name);
        Assert.Equal("vbeami_vcam", locked.EffectiveFolder);
        Assert.Equal("vcam", fileSystem.ReadAllText($"{ProjectDir}/addons/vbeami_vcam/plugin.cfg"));

        // The first addon is left exactly where it was.
        Assert.Equal("core", fileSystem.ReadAllText($"{ProjectDir}/addons/vbeami/plugin.cfg"));

        var manifest = ReadManifest(fileSystem);
        Assert.Equal(["vbeami", "vbeami_vcam"], manifest.Addons.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("godot/addons/vbeami_vcam", manifest.Addons["vbeami_vcam"].Path);
        Assert.Equal("vbeami_vcam", manifest.Addons["vbeami_vcam"].InstallAs);
        Assert.Equal("godot/addons/vbeami", manifest.Addons["vbeami"].Path);
    }

    [Fact]
    public async Task AddAsync_SecondSubtreeWithoutInstallAs_IsNamedAndInstalledAfterItsPathLeaf() {
        var (workflow, fileSystem) = Build();
        await AddCore(workflow);

        var locked = await workflow.AddAsync(
            ProjectDir, RepoUrl,
            new AddonAddOptions { Branch = "main", Path = "godot/addons/vbeami_gdmp" });

        // The name doubles as the install folder, so it lands in its own folder rather than on top
        // of addons/vbeami.
        Assert.Equal("vbeami_gdmp", locked.Name);
        Assert.Equal("gdmp", fileSystem.ReadAllText($"{ProjectDir}/addons/vbeami_gdmp/plugin.cfg"));
        Assert.Equal("core", fileSystem.ReadAllText($"{ProjectDir}/addons/vbeami/plugin.cfg"));
    }

    [Fact]
    public async Task AddAsync_ExplicitName_IsWrittenAsTheKey() {
        var (workflow, fileSystem) = Build();
        await AddCore(workflow);

        var locked = await workflow.AddAsync(
            ProjectDir,
            RepoUrl,
            new AddonAddOptions {
                Branch = "main",
                Path = "godot/addons/vbeami_vcam",
                InstallAs = "vbeami_vcam",
                Name = "camera"
            });

        Assert.Equal("camera", locked.Name);
        Assert.Equal("vbeami_vcam", locked.EffectiveFolder);
        Assert.True(ReadManifest(fileSystem).Addons.ContainsKey("camera"));
    }

    [Fact]
    public async Task AddAsync_ThreeAddonsFromOneRepo_AllCoexist() {
        var (workflow, fileSystem) = Build();
        await AddCore(workflow);
        await workflow.AddAsync(ProjectDir, RepoUrl,
            new AddonAddOptions { Branch = "main", Path = "godot/addons/vbeami_gdmp" });
        await workflow.AddAsync(
            ProjectDir, RepoUrl,
            new AddonAddOptions {
                Branch = "main",
                Path = "godot/addons/vbeami_vcam",
                InstallAs = "vbeami_vcam"
            });

        Assert.Equal(
            ["vbeami", "vbeami_gdmp", "vbeami_vcam"],
            ReadManifest(fileSystem).Addons.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AddAsync_SameSubtreeAgain_StillRefusesWithUpdateHint() {
        var (workflow, _) = Build();
        await AddCore(workflow);

        var exception = await Assert.ThrowsAsync<WorkflowException>(() => AddCore(workflow));

        Assert.Contains("dotquay update vbeami", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewAddAsync_SecondSubtree_ReportsTheNameTheAddWouldWrite() {
        var (workflow, fileSystem) = Build();
        await AddCore(workflow);
        var manifestBefore = fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml");

        var preview = await workflow.PreviewAddAsync(
            ProjectDir,
            RepoUrl,
            new AddonAddOptions {
                Branch = "main",
                Path = "godot/addons/vbeami_vcam",
                InstallAs = "vbeami_vcam"
            });

        // Preview and add share the naming code; a preview reporting "vbeami" here would invite a
        // confirm button that then fails.
        Assert.Equal("vbeami_vcam", preview.Name);
        Assert.Equal(manifestBefore, fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"));
    }
}
