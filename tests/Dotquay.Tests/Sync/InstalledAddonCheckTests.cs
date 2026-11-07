using Dotquay.Core.Abstractions;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

/// <summary>
/// Reinstalling a commit that is already on disk, untouched, must not rewrite the folder - and every
/// way it is *not* untouched must still take the full install path, refusals included.
/// </summary>
public class InstalledAddonCheckTests {
    private const string RepoUrl = "https://example.com/dialogue_manager.git";
    private const string Sha = "1111111111111111111111111111111111aaaa";
    private const string NewSha = "2222222222222222222222222222222222bbbb";
    private const string Project = "project";
    private const string Folder = "project/addons/dialogue_manager";

    private static readonly ProjectLayout Layout = ProjectLayout.WithoutPatches(Project);

    private static (AddonInstaller Installer, InMemoryFileSystem FileSystem, FakeGitClient GitClient) Build() {
        var fileSystem = new InMemoryFileSystem();
        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[RepoUrl] = new Dictionary<string, string> {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
            ["addons/dialogue_manager/dialogue_manager.gd"] = "gd source",
        };

        var cache = new RepoCache(fileSystem, gitClient, "cache");
        var installer = new AddonInstaller(fileSystem, cache, new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        return (installer, fileSystem, gitClient);
    }

    private static ResolvedAddon Resolved(string sha = Sha, string? displayVersion = "v2.44.0", string? installAs = null) =>
        new("dialogue_manager", RepoUrl, sha, displayVersion, "addons/dialogue_manager", installAs);

    private static async Task<(AddonInstaller Installer, InMemoryFileSystem FileSystem, FakeGitClient Git, LockedAddon Locked)> InstalledOnce() {
        var (installer, fileSystem, git) = Build();
        var locked = await installer.InstallAsync(Resolved(), Layout, previous: null, force: false);
        fileSystem.DirectoryMoves.Clear();
        return (installer, fileSystem, git, locked);
    }

    private static int MovesInto(InMemoryFileSystem fileSystem, string folder) =>
        fileSystem.DirectoryMoves.Count(m => m.Destination == folder);

    // --- the installer -------------------------------------------------------------------------------

    [Fact]
    public async Task SameCommit_CleanFolder_IsLeftAlone_AndYieldsTheSameEntry() {
        var (installer, fileSystem, _, first) = await InstalledOnce();

        var second = await installer.InstallAsync(Resolved(), Layout, first, force: false);

        Assert.Equal(0, MovesInto(fileSystem, Folder));
        Assert.Equal(first, second);
        Assert.Equal("gd source", fileSystem.ReadAllText($"{Folder}/dialogue_manager.gd"));
    }

    [Fact]
    public async Task SameCommit_StillRecomputesTheDisplayVersion_ExactlyAsAFullInstallWould() {
        var (installer, _, git, first) = await InstalledOnce();
        git.CommitDates[Sha] = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var stale = first with { Version = "0.0.0-20200101-1111111" };

        var second = await installer.InstallAsync(Resolved(displayVersion: null), Layout, stale, force: false);

        Assert.Equal("0.0.0-20260930-1111111", second.Version);
    }

    [Fact]
    public async Task SameCommit_EditorRewroteAnImportFile_StillInPlace_AndTheRewriteIsKept() {
        var (installer, fileSystem, _, first) = await InstalledOnce();
        fileSystem.WriteAllText($"{Folder}/icon.png.import", "[remap]\nimporter=\"texture\"");

        await installer.InstallAsync(Resolved(), Layout, first, force: false);

        Assert.Equal(0, MovesInto(fileSystem, Folder));
        Assert.Equal("[remap]\nimporter=\"texture\"", fileSystem.ReadAllText($"{Folder}/icon.png.import"));
    }

    [Fact]
    public async Task SameCommit_HandEditedFolder_IsStillRefused() {
        var (installer, fileSystem, _, first) = await InstalledOnce();
        fileSystem.WriteAllText($"{Folder}/dialogue_manager.gd", "hand-edited");

        await Assert.ThrowsAsync<ModifiedAddonException>(
            () => installer.InstallAsync(Resolved(), Layout, first, force: false));

        Assert.Equal("hand-edited", fileSystem.ReadAllText($"{Folder}/dialogue_manager.gd"));
    }

    [Fact]
    public async Task SameCommit_HandEditedFolder_WithForce_IsOverwritten() {
        var (installer, fileSystem, _, first) = await InstalledOnce();
        fileSystem.WriteAllText($"{Folder}/dialogue_manager.gd", "hand-edited");

        await installer.InstallAsync(Resolved(), Layout, first, force: true);

        Assert.Equal(1, MovesInto(fileSystem, Folder));
        Assert.Equal("gd source", fileSystem.ReadAllText($"{Folder}/dialogue_manager.gd"));
    }

    [Fact]
    public async Task SameCommit_FolderDeleted_IsReinstalled() {
        var (installer, fileSystem, _, first) = await InstalledOnce();
        fileSystem.DeleteDirectory(Folder);

        await installer.InstallAsync(Resolved(), Layout, first, force: false);

        Assert.Equal(1, MovesInto(fileSystem, Folder));
        Assert.Equal("cfg", fileSystem.ReadAllText($"{Folder}/plugin.cfg"));
    }

    [Fact]
    public async Task NewCommit_IsInstalled() {
        var (installer, fileSystem, git, first) = await InstalledOnce();
        git.FetchedFilesByUrlAndSha[(RepoUrl, NewSha)] = new Dictionary<string, string> {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg v2",
        };

        var second = await installer.InstallAsync(Resolved(NewSha), Layout, first, force: false);

        Assert.Equal(1, MovesInto(fileSystem, Folder));
        Assert.Equal(NewSha, second.Resolved);
        Assert.Equal("cfg v2", fileSystem.ReadAllText($"{Folder}/plugin.cfg"));
    }

    // --- the check's conditions, one at a time -----------------------------------------------------

    [Fact]
    public async Task Check_NoPreviousRecord_IsNotInPlace() {
        var (_, fileSystem, _, _) = await InstalledOnce();

        Assert.False(InstalledAddonCheck.IsAlreadyInPlace(fileSystem, Layout, Resolved(), previous: null));
    }

    [Fact]
    public async Task Check_PreviousWithoutContentHash_IsNotInPlace() {
        var (_, fileSystem, _, locked) = await InstalledOnce();

        Assert.False(InstalledAddonCheck.IsAlreadyInPlace(fileSystem, Layout, Resolved(), locked with { ContentHash = null }));
    }

    [Fact]
    public async Task Check_CleanAndIdentical_IsInPlace() {
        var (_, fileSystem, _, locked) = await InstalledOnce();

        Assert.True(InstalledAddonCheck.IsAlreadyInPlace(fileSystem, Layout, Resolved(), locked));
    }

    [Theory]
    [InlineData("git")]
    [InlineData("sha")]
    [InlineData("path")]
    [InlineData("folder")]
    public async Task Check_AnyIdentityFieldDiffers_IsNotInPlace(string field) {
        var (_, fileSystem, _, locked) = await InstalledOnce();
        var previous = field switch {
            "git" => locked with { Git = "https://example.com/fork.git" },
            "sha" => locked with { Resolved = NewSha },
            "path" => locked with { Path = "src/dialogue_manager" },
            "folder" => locked with { Folder = "DialogueManager" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.False(InstalledAddonCheck.IsAlreadyInPlace(fileSystem, Layout, Resolved(), previous));
    }

    [Fact]
    public async Task Check_PatchedAddon_IsNotInPlace_SoThePatchIsReappliedAsBefore() {
        var (_, fileSystem, _, locked) = await InstalledOnce();
        var patched = locked with { PatchHash = "sha256:patch", PatchBaseSha = Sha };

        Assert.False(InstalledAddonCheck.IsAlreadyInPlace(fileSystem, Layout, Resolved(), patched));
    }

    [Fact]
    public async Task Check_PendingConflictMarker_IsNotInPlace() {
        var (_, fileSystem, _, locked) = await InstalledOnce();
        fileSystem.WriteAllText(Layout.ConflictMarkerPath(fileSystem, "dialogue_manager"), NewSha);

        Assert.False(InstalledAddonCheck.IsAlreadyInPlace(fileSystem, Layout, Resolved(), locked));
    }

    [Fact]
    public async Task Check_DirectoryLinkWhereTheFolderShouldBe_IsNotInPlace() {
        // The full path must see it, because the full path is what refuses to write through a link
        // with an explanation (v0.16). Calling it "in place" would skip that refusal silently.
        var (_, fileSystem, _, locked) = await InstalledOnce();
        fileSystem.MoveDirectory(Folder, "elsewhere/checkout");
        fileSystem.CreateDirectoryLink(Folder, "elsewhere/checkout");

        Assert.False(InstalledAddonCheck.IsAlreadyInPlace(fileSystem, Layout, Resolved(), locked));
    }

    [Fact]
    public async Task Check_DoesNotWriteAnything() {
        var (_, fileSystem, _, locked) = await InstalledOnce();
        var before = fileSystem.AllFilePaths().Order().ToList();

        InstalledAddonCheck.IsAlreadyInPlace(fileSystem, Layout, Resolved(), locked);

        Assert.Equal(before, fileSystem.AllFilePaths().Order().ToList());
        Assert.Empty(fileSystem.DirectoryMoves);
    }

    // --- through the whole update ------------------------------------------------------------------

    [Fact]
    public async Task Update_WithNothingNew_RewritesNoAddonFolder_AndLeavesTheLockfileAsItWas() {
        var (workflow, fileSystem) = BuildWorkflow();
        await workflow.AddAsync(Project, RepoUrl);
        var lockBefore = fileSystem.ReadAllText($"{Project}/dotquay.lock");
        fileSystem.DirectoryMoves.Clear();

        await workflow.UpdateAsync(Project, name: null, force: false);

        Assert.Equal(0, MovesInto(fileSystem, Folder));
        Assert.Equal(lockBefore, fileSystem.ReadAllText($"{Project}/dotquay.lock"));
    }

    [Fact]
    public async Task Update_WithNothingNew_ButAHandEdit_StillRefuses() {
        var (workflow, fileSystem) = BuildWorkflow();
        await workflow.AddAsync(Project, RepoUrl);
        fileSystem.WriteAllText($"{Folder}/plugin.cfg", "hand-edited");

        await Assert.ThrowsAsync<WorkflowException>(() => workflow.UpdateAsync(Project, name: null, force: false));

        Assert.Equal("hand-edited", fileSystem.ReadAllText($"{Folder}/plugin.cfg"));
    }

    private static (DependencyWorkflow Workflow, InMemoryFileSystem FileSystem) BuildWorkflow() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Project}/dotquay.toml", "[project]\n");

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.RemoteRefsByUrl[RepoUrl] = [new GitRef("v1.0.0", new string('1', 40), GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[RepoUrl] = new Dictionary<string, string> {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
        };

        return (new DependencyWorkflow(fileSystem, gitClient, "cache"), fileSystem);
    }
}
