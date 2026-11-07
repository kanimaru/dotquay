using Dotquay.Core.Abstractions;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers a transitive addon whose install folder changes because its <em>parent</em> changed
/// (SPEC.md §7.1, §8).
/// </summary>
/// <remarks>
/// The graph sync skips reinstalling an addon it considers unchanged, and that check originally
/// compared only the git URL and the resolved commit. For a transitive addon pinned by <c>rev</c>,
/// a parent that starts declaring <c>install_as</c> moves nothing about the commit — so the addon
/// was skipped and stayed in its old folder while the command reported success.
/// <para>
/// Found in the field: vbeami began declaring
/// <c>install_as = "Godot-MToon-Shader"</c> for mtoon-shader, a consuming project ran
/// <c>update</c>, and the shader stayed in <c>addons/mtoon-shader</c> — where vrm's
/// <c>preload("res://addons/Godot-MToon-Shader/…")</c> could not find it. Top-level addons never
/// hit this, because a full update marks every root fresh; only transitive ones were exposed.
/// </para>
/// </remarks>
public class TransitiveInstallAsTests {
    private const string ProjectDirectory = "project";
    private const string ParentUrl = "https://example.com/parent.git";
    private const string ChildUrl = "https://example.com/child.git";

    private static string ParentSha => new('a', 40);

    private static string ChildSha => new('c', 40);

    /// <param name="childInstallAs">What the parent's package manifest says about the child's
    /// folder, or null for the pre-fix manifest that said nothing.</param>
    private static (DependencyWorkflow Workflow,
        InMemoryFileSystem FileSystem) Build(string? childInstallAs) {
        var fileSystem = new InMemoryFileSystem();
        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };

        var installAsLine = childInstallAs is null ? string.Empty
            : $"install_as = \"{childInstallAs}\"\n";

        gitClient.RemoteRefsByUrl[ParentUrl] = [new GitRef("v1.0.0", ParentSha, GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[ParentUrl] = new Dictionary<string, string> {
            ["addons/parent/plugin.cfg"] = "cfg",
            ["addons/parent/dotquay.toml"] =
                "[package]\nname = \"parent\"\n\n[addons.child]\n"
                + $"git = \"{ChildUrl}\"\nrev = \"{ChildSha}\"\npath = \".\"\n{installAsLine}",
        };

        gitClient.FetchedFilesByUrl[ChildUrl] = new Dictionary<string,
            string> { ["plugin.cfg"] = "child cfg" };

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), $"""
            [addons.parent]
            git = "{ParentUrl}"
            version = "~1"
            path = "addons/parent"
            """);

        var cache = new RepoCache(fileSystem, gitClient, "cache");
        var installer = new AddonInstaller(fileSystem, cache, new SyncEngine(fileSystem),
            new FakePatchTool(fileSystem));
        var workflow = new DependencyWorkflow(fileSystem, gitClient, installer);

        return (workflow, fileSystem);
    }

    private static bool HasFolder(IFileSystem fileSystem, string folder) =>
        fileSystem.DirectoryExists(fileSystem.CombinePath(ProjectDirectory, "addons", folder));

    [Fact]
    public async Task UpdateAsync_ParentDeclaringInstallAs_PutsTheChildThere() {
        var (workflow, fileSystem) = Build(childInstallAs: "Renamed-Child");

        await workflow.UpdateAsync(ProjectDirectory, name: null, force: false);

        Assert.True(HasFolder(fileSystem, "Renamed-Child"));
        Assert.False(HasFolder(fileSystem, "child"));
    }

    [Fact]
    public async Task UpdateAsync_ParentAddsInstallAsLater_MovesAnAlreadyInstalledChild() {
        // First install with the old parent manifest, which named no folder.
        var (before, beforeFs) = Build(childInstallAs: null);
        await before.UpdateAsync(ProjectDirectory, name: null, force: false);
        Assert.True(HasFolder(beforeFs, "child"));

        // The upgrade: same child commit, same everything — only the parent's manifest changed.
        var lockfile = beforeFs.ReadAllText(beforeFs.CombinePath(ProjectDirectory, "dotquay.lock"));
        var (after, afterFs) = Build(childInstallAs: "Renamed-Child");
        afterFs.WriteAllText(afterFs.CombinePath(ProjectDirectory, "dotquay.lock"), lockfile);
        afterFs.WriteAllText(afterFs.CombinePath(ProjectDirectory, "addons", "child", "plugin.cfg"),
            "child cfg");

        await after.UpdateAsync(ProjectDirectory, name: null, force: false);

        // The regression: the child's commit never moved, so the sync called it unchanged and
        // skipped the reinstall entirely — leaving it in the folder the parent just stopped asking
        // for.
        Assert.True(HasFolder(afterFs, "Renamed-Child"));
        Assert.False(HasFolder(afterFs, "child"));
    }

    [Fact]
    public async Task UpdateAsync_ParentAddsInstallAsLater_RecordsTheFolderInTheLockfile() {
        var (before, beforeFs) = Build(childInstallAs: null);
        await before.UpdateAsync(ProjectDirectory, name: null, force: false);

        var lockfile = beforeFs.ReadAllText(beforeFs.CombinePath(ProjectDirectory, "dotquay.lock"));
        var (after, afterFs) = Build(childInstallAs: "Renamed-Child");
        afterFs.WriteAllText(afterFs.CombinePath(ProjectDirectory, "dotquay.lock"), lockfile);
        afterFs.WriteAllText(afterFs.CombinePath(afterFs.CombinePath(ProjectDirectory, "addons",
            "child"), "plugin.cfg"), "child cfg");

        await after.UpdateAsync(ProjectDirectory, name: null, force: false);

        // Without this, `install` on a fresh clone would replay the old folder and the fix would
        // only hold on machines that happened to run `update`.
        var parsed = LockfileReader.Read(afterFs.ReadAllText(afterFs.CombinePath(ProjectDirectory,
            "dotquay.lock")));
        Assert.Equal("Renamed-Child", parsed.Addons.Single(a => a.Name == "child").EffectiveFolder);
    }

    [Fact]
    public async Task UpdateAsync_NothingChanged_StillSkipsTheReinstall() {
        // The optimisation the fix must not defeat: an genuinely unchanged transitive addon should
        // not be refetched and reinstalled on every update.
        var (before, beforeFs) = Build(childInstallAs: "Renamed-Child");
        await before.UpdateAsync(ProjectDirectory, name: null, force: false);

        var lockfile = beforeFs.ReadAllText(beforeFs.CombinePath(ProjectDirectory, "dotquay.lock"));
        var (after, afterFs) = Build(childInstallAs: "Renamed-Child");
        afterFs.WriteAllText(afterFs.CombinePath(ProjectDirectory, "dotquay.lock"), lockfile);

        var result = await after.UpdateAsync(ProjectDirectory, name: null, force: false);

        Assert.DoesNotContain(result.Addons, a => a.Name == "child" && a.Changed);
    }

    [Fact]
    public async Task SyncAddonGraph_PrunesAnOrphanByItsRealFolder() {
        var (workflow, fileSystem) = Build(childInstallAs: "Renamed-Child");
        await workflow.UpdateAsync(ProjectDirectory, name: null, force: false);
        Assert.True(HasFolder(fileSystem, "Renamed-Child"));

        // Drop the parent, so the child becomes unreachable and should be pruned.
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), "");
        await workflow.UpdateAsync(ProjectDirectory, name: null, force: false);

        // Pruning by addon name rather than install folder would delete addons/child — which does
        // not exist — and leave the real folder behind for Godot to keep loading.
        Assert.False(HasFolder(fileSystem, "Renamed-Child"));
    }
}
