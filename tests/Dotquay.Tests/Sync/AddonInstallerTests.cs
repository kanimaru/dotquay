using Dotquay.Core.Lockfile;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

public class AddonInstallerTests
{
    private const string RepoUrl = "https://example.com/dialogue_manager.git";
    private const string Sha = "1111111111111111111111111111111111aaaa";
    private const string NewSha = "2222222222222222222222222222222222bbbb";

    private static (AddonInstaller Installer, InMemoryFileSystem FileSystem, FakeGitClient GitClient, FakePatchTool PatchTool) Build()
    {
        var fileSystem = new InMemoryFileSystem();
        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[RepoUrl] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
            ["addons/dialogue_manager/dialogue_manager.gd"] = "gd source",
        };

        var cache = new RepoCache(fileSystem, gitClient, "cache");
        var patchTool = new FakePatchTool(fileSystem);
        var installer = new AddonInstaller(fileSystem, cache, new SyncEngine(fileSystem), patchTool);
        return (installer, fileSystem, gitClient, patchTool);
    }

    [Fact]
    public async Task InstallAsync_FreshInstall_FetchesExtractsAndReturnsLockedAddon()
    {
        var (installer, fileSystem, gitClient, _) = Build();
        var resolved = new ResolvedAddon("dialogue_manager", RepoUrl, Sha, DisplayVersion: "v2.44.0", "addons/dialogue_manager");

        var locked = await installer.InstallAsync(resolved, ProjectLayout.WithoutPatches("project"), previous: null, force: false);

        Assert.Equal("dialogue_manager", locked.Name);
        Assert.Equal(Sha, locked.Resolved);
        Assert.Equal("v2.44.0", locked.Version);
        Assert.StartsWith("sha256:", locked.ContentHash);
        Assert.Null(locked.PatchHash);
        Assert.Null(locked.PatchBaseSha);
        Assert.True(fileSystem.FileExists("project/addons/dialogue_manager/plugin.cfg"));
        Assert.Equal("cfg", fileSystem.ReadAllText("project/addons/dialogue_manager/plugin.cfg"));
        Assert.Single(gitClient.FetchCalls);
    }

    [Fact]
    public async Task InstallAsync_NoDisplayVersion_GeneratesPseudoVersionFromShortSha()
    {
        var (installer, _, _, _) = Build();
        var resolved = new ResolvedAddon("dialogue_manager", RepoUrl, Sha, DisplayVersion: null, "addons/dialogue_manager");

        var locked = await installer.InstallAsync(resolved, ProjectLayout.WithoutPatches("project"), previous: null, force: false);

        Assert.Matches(@"^0\.0\.0-\d{8}-1111111$", locked.Version);
    }

    [Fact]
    public async Task InstallAsync_DirtyDestinationWithoutForce_ThrowsSyncException()
    {
        var (installer, fileSystem, _, _) = Build();
        fileSystem.WriteAllText("project/addons/dialogue_manager/plugin.cfg", "hand-edited");
        var resolved = new ResolvedAddon("dialogue_manager", RepoUrl, Sha, DisplayVersion: null, "addons/dialogue_manager");

        await Assert.ThrowsAsync<SyncException>(
            () => installer.InstallAsync(resolved, ProjectLayout.WithoutPatches("project"), PreviousWithContentHash("sha256:original"), force: false));
    }

    [Fact]
    public async Task InstallAsync_RefusedInstall_LeavesNoStagedTree()
    {
        // A refusal is final for this run, so the extracted tree has no further use. Leaving it made
        // .dotquay/staging accumulate full copies of addons nobody would ever finish installing.
        var (installer, fileSystem, _, _) = Build();
        fileSystem.WriteAllText("project/addons/dialogue_manager/plugin.cfg", "hand-edited");
        var resolved = new ResolvedAddon("dialogue_manager", RepoUrl, Sha, DisplayVersion: null, "addons/dialogue_manager");

        await Assert.ThrowsAsync<SyncException>(
            () => installer.InstallAsync(resolved, ProjectLayout.WithoutPatches("project"), PreviousWithContentHash("sha256:original"), force: false));

        Assert.False(fileSystem.DirectoryExists("project/.dotquay/staging/dialogue_manager"));
    }

    [Fact]
    public async Task InstallAsync_DirtyDestinationWithForce_Overwrites()
    {
        var (installer, fileSystem, _, _) = Build();
        fileSystem.WriteAllText("project/addons/dialogue_manager/plugin.cfg", "hand-edited");
        var resolved = new ResolvedAddon("dialogue_manager", RepoUrl, Sha, DisplayVersion: null, "addons/dialogue_manager");

        await installer.InstallAsync(resolved, ProjectLayout.WithoutPatches("project"), PreviousWithContentHash("sha256:original"), force: true);

        Assert.Equal("cfg", fileSystem.ReadAllText("project/addons/dialogue_manager/plugin.cfg"));
    }

    [Fact]
    public async Task InstallAsync_ReusesCacheOnSecondCall_FetchesOnlyOnce()
    {
        var (installer, _, gitClient, _) = Build();
        var resolved = new ResolvedAddon("dialogue_manager", RepoUrl, Sha, DisplayVersion: null, "addons/dialogue_manager");

        await installer.InstallAsync(resolved, ProjectLayout.WithoutPatches("project-a"), previous: null, force: false);
        await installer.InstallAsync(resolved, ProjectLayout.WithoutPatches("project-b"), previous: null, force: false);

        Assert.Single(gitClient.FetchCalls);
    }

    [Fact]
    public async Task InstallAsync_PatchedAddonCleanUpdate_ReappliesPatchAndCarriesForwardMetadata()
    {
        var (installer, fileSystem, gitClient, patchTool) = Build();

        gitClient.FetchedFilesByUrlAndSha[(RepoUrl, Sha)] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
        };
        gitClient.FetchedFilesByUrlAndSha[(RepoUrl, NewSha)] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg", // upstream left this file alone
            ["addons/dialogue_manager/new_feature.gd"] = "new upstream feature", // upstream added this
        };

        // The currently-installed addon already has the patch's hand-edit baked in (a prior
        // install/patch cycle) — the dirty check needs this to match previous.ContentHash exactly.
        fileSystem.WriteAllText("project/addons/dialogue_manager/plugin.cfg", "cfg-edited");
        var previousContentHash = ContentHasher.HashDirectory(fileSystem, "project/addons/dialogue_manager");

        fileSystem.WriteAllText("project/.dotquay/patches/dialogue_manager.patch", "fake-patch-text");
        patchTool.FileChangesByPatchText["fake-patch-text"] = new Dictionary<string, string>
        {
            ["plugin.cfg"] = "cfg-edited",
        };

        var previous = new LockedAddon
        {
            Name = "dialogue_manager",
            Git = RepoUrl,
            Resolved = Sha,
            Path = "addons/dialogue_manager",
            ContentHash = previousContentHash,
            PatchHash = "sha256:deadbeef",
            PatchBaseSha = Sha,
        };
        var resolved = new ResolvedAddon("dialogue_manager", RepoUrl, NewSha, DisplayVersion: null, "addons/dialogue_manager");

        var locked = await installer.InstallAsync(resolved, ProjectLayout.WithoutPatches("project"), previous, force: false);

        Assert.Equal(NewSha, locked.Resolved);
        Assert.Equal("sha256:deadbeef", locked.PatchHash);
        Assert.Equal(Sha, locked.PatchBaseSha);
        Assert.Equal("cfg-edited", fileSystem.ReadAllText("project/addons/dialogue_manager/plugin.cfg"));
        Assert.Equal("new upstream feature", fileSystem.ReadAllText("project/addons/dialogue_manager/new_feature.gd"));
    }

    [Fact]
    public async Task InstallAsync_PatchedAddonConflictingUpdate_ThrowsAndLeavesAddonsUntouched()
    {
        var (installer, fileSystem, gitClient, patchTool) = Build();

        gitClient.FetchedFilesByUrlAndSha[(RepoUrl, Sha)] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
        };
        gitClient.FetchedFilesByUrlAndSha[(RepoUrl, NewSha)] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg-upstream-changed", // upstream touched it too
        };

        fileSystem.WriteAllText("project/addons/dialogue_manager/plugin.cfg", "cfg-edited");
        var previousContentHash = ContentHasher.HashDirectory(fileSystem, "project/addons/dialogue_manager");

        fileSystem.WriteAllText("project/.dotquay/patches/dialogue_manager.patch", "fake-patch-text");
        patchTool.FileChangesByPatchText["fake-patch-text"] = new Dictionary<string, string>
        {
            ["plugin.cfg"] = "cfg-edited",
        };
        // base/ours/theirs all differ pairwise for plugin.cfg -> PatchMerger's fast paths don't apply,
        // so it falls through to a real 3-way merge attempt, which this forces to conflict.
        patchTool.DefaultConflictCount = 1;

        var previous = new LockedAddon
        {
            Name = "dialogue_manager",
            Git = RepoUrl,
            Resolved = Sha,
            Path = "addons/dialogue_manager",
            ContentHash = previousContentHash,
            PatchHash = "sha256:deadbeef",
            PatchBaseSha = Sha,
        };
        var resolved = new ResolvedAddon("dialogue_manager", RepoUrl, NewSha, DisplayVersion: null, "addons/dialogue_manager");

        var ex = await Assert.ThrowsAsync<SyncException>(() => installer.InstallAsync(resolved, ProjectLayout.WithoutPatches("project"), previous, force: false));

        Assert.Contains("plugin.cfg", ex.Message);
        Assert.Contains("dialogue_manager", ex.Message);

        // addons/ was never touched by the failed swap.
        Assert.Equal("cfg-edited", fileSystem.ReadAllText("project/addons/dialogue_manager/plugin.cfg"));

        // The conflicted merge result is left behind in staging for the user to resolve.
        Assert.True(fileSystem.FileExists("project/.dotquay/staging/dialogue_manager/plugin.cfg"));
    }

    [Fact]
    public async Task InstallAsync_PatchDeletesFileUpstreamLeftAlone_CleanlyOmitsItFromResult()
    {
        var (installer, fileSystem, gitClient, patchTool) = Build();

        gitClient.FetchedFilesByUrlAndSha[(RepoUrl, Sha)] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
            ["addons/dialogue_manager/extra.gd"] = "extra",
        };
        gitClient.FetchedFilesByUrlAndSha[(RepoUrl, NewSha)] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
            ["addons/dialogue_manager/extra.gd"] = "extra", // upstream didn't touch the file our patch deletes
        };

        fileSystem.WriteAllText("project/addons/dialogue_manager/plugin.cfg", "cfg");
        var previousContentHash = ContentHasher.HashDirectory(fileSystem, "project/addons/dialogue_manager");

        fileSystem.WriteAllText("project/.dotquay/patches/dialogue_manager.patch", "fake-delete-patch");
        patchTool.FileDeletionsByPatchText["fake-delete-patch"] = ["extra.gd"];

        var previous = new LockedAddon
        {
            Name = "dialogue_manager",
            Git = RepoUrl,
            Resolved = Sha,
            Path = "addons/dialogue_manager",
            ContentHash = previousContentHash,
            PatchHash = "sha256:deadbeef",
            PatchBaseSha = Sha,
        };
        var resolved = new ResolvedAddon("dialogue_manager", RepoUrl, NewSha, DisplayVersion: null, "addons/dialogue_manager");

        await installer.InstallAsync(resolved, ProjectLayout.WithoutPatches("project"), previous, force: false);

        Assert.True(fileSystem.FileExists("project/addons/dialogue_manager/plugin.cfg"));
        Assert.False(fileSystem.FileExists("project/addons/dialogue_manager/extra.gd"));
    }

    [Fact]
    public async Task InstallAsync_LockfileSaysPatchedButPatchFileMissing_InstallsWithoutReapplying()
    {
        var (installer, fileSystem, gitClient, _) = Build();
        gitClient.FetchedFilesByUrlAndSha[(RepoUrl, NewSha)] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg-from-upstream",
        };

        fileSystem.WriteAllText("project/addons/dialogue_manager/plugin.cfg", "cfg-edited");
        var previousContentHash = ContentHasher.HashDirectory(fileSystem, "project/addons/dialogue_manager");
        // Deliberately no .dotquay/patches/dialogue_manager.patch written.

        var previous = new LockedAddon
        {
            Name = "dialogue_manager",
            Git = RepoUrl,
            Resolved = Sha,
            Path = "addons/dialogue_manager",
            ContentHash = previousContentHash,
            PatchHash = "sha256:deadbeef",
            PatchBaseSha = Sha,
        };
        var resolved = new ResolvedAddon("dialogue_manager", RepoUrl, NewSha, DisplayVersion: null, "addons/dialogue_manager");

        var locked = await installer.InstallAsync(resolved, ProjectLayout.WithoutPatches("project"), previous, force: true);

        Assert.Equal("cfg-from-upstream", fileSystem.ReadAllText("project/addons/dialogue_manager/plugin.cfg"));
        Assert.Equal("sha256:deadbeef", locked.PatchHash);
    }

    private static LockedAddon PreviousWithContentHash(string contentHash) => new()
    {
        Name = "dialogue_manager",
        Git = RepoUrl,
        Resolved = Sha,
        Path = "addons/dialogue_manager",
        ContentHash = contentHash,
    };
}
