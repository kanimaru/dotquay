using Dotquay.Core.Abstractions;
using Dotquay.Core.Local;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Local;

/// <summary>
/// Covers local links (SPEC.md §7.4) — developing an addon in place while a project consumes it.
/// </summary>
/// <remarks>
/// The feature exists because resolution is commit-based by design (SPEC.md §5), which makes the
/// addon-author's loop commit → push → update for every edit. A link trades that for a directory link
/// into the working checkout.
/// <para>
/// The tests that matter most here are the destructive ones. <c>addons/&lt;folder&gt;</c> gets deleted
/// during orphan pruning, <c>remove</c>, and the <c>install_as</c> rename; when it is a link, that
/// folder is somebody's actual source checkout, and a recursive delete through it would destroy
/// uncommitted work. Nothing else in dotquay can lose data that isn't recoverable from git.
/// </para>
/// </remarks>
public class LocalLinkTests
{
    private const string ProjectDirectory = "project";
    private const string SourceDirectory = "elsewhere/my-addon";
    private const string RepoUrl = "https://example.com/my-addon.git";

    private static string Sha => new('a', 40);

    private static InMemoryFileSystem BuildFileSystemWithSource()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(SourceDirectory, "plugin.cfg"), "[plugin]");
        fileSystem.WriteAllText(fileSystem.CombinePath(SourceDirectory, "thing.gd"), "# live edit");
        return fileSystem;
    }

    private static AddonInstaller BuildInstaller(InMemoryFileSystem fileSystem)
    {
        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[RepoUrl] = new Dictionary<string, string>
        {
            ["plugin.cfg"] = "[plugin]",
            ["thing.gd"] = "# committed version",
        };

        return new AddonInstaller(fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
    }

    private static ResolvedAddon Resolved() => new("my-addon", RepoUrl, Sha, DisplayVersion: null, Path: ".");

    /// <summary>The same addon asking to be installed under a different folder (SPEC.md §7.1).</summary>
    private static ResolvedAddon Renamed(string installAs) =>
        new("my-addon", RepoUrl, Sha, DisplayVersion: null, Path: ".", InstallAs: installAs);

    private static string InstalledPath(IFileSystem fileSystem) =>
        fileSystem.CombinePath(ProjectDirectory, "addons", "my-addon");

    // -----------------------------------------------------------------------------------------
    // Reading dotquay.local.toml
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Read_NoFile_IsEmpty()
    {
        var links = new LocalLinks(new InMemoryFileSystem());

        // The overwhelmingly common case. Not having links is not a condition worth reporting.
        Assert.Empty(links.Read(ProjectDirectory));
    }

    [Fact]
    public void SetThenRead_RoundTrips()
    {
        var fileSystem = BuildFileSystemWithSource();
        var links = new LocalLinks(fileSystem);

        links.Set(ProjectDirectory, "my-addon", SourceDirectory);

        var link = Assert.Single(links.Read(ProjectDirectory)).Value;
        Assert.Equal("my-addon", link.Name);
        Assert.Contains("my-addon", link.Path);
    }

    [Fact]
    public void Set_RejectsAPathThatIsNotADirectory()
    {
        var links = new LocalLinks(new InMemoryFileSystem());

        var exception = Assert.Throws<LocalLinkException>(() => links.Set(ProjectDirectory, "my-addon", "nope/missing"));
        Assert.Contains("not a directory", exception.Message);
    }

    [Fact]
    public void Remove_LastLink_DeletesTheFile()
    {
        var fileSystem = BuildFileSystemWithSource();
        var links = new LocalLinks(fileSystem);
        links.Set(ProjectDirectory, "my-addon", SourceDirectory);

        Assert.True(links.Remove(ProjectDirectory, "my-addon"));

        // An empty file suggests something is configured when nothing is.
        Assert.False(fileSystem.FileExists(links.PathFor(ProjectDirectory)));
    }

    [Fact]
    public void Remove_SomethingNotLinked_ReportsNoChange()
    {
        var links = new LocalLinks(new InMemoryFileSystem());

        Assert.False(links.Remove(ProjectDirectory, "my-addon"));
    }

    [Fact]
    public void Read_MalformedFile_Throws()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, LocalLinks.FileName), "[link.my-addon]\n");

        // The file exists only because someone deliberately made it; ignoring a mistake in it would
        // leave them watching an addon refuse to link with nothing said about why.
        var exception = Assert.Throws<LocalLinkException>(() => new LocalLinks(fileSystem).Read(ProjectDirectory));
        Assert.Contains("'path'", exception.Message);
    }

    // -----------------------------------------------------------------------------------------
    // Installing as a link
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task InstallAsync_WithALink_PointsAtTheSource()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);

        await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, new LocalLink("my-addon", SourceDirectory));

        Assert.True(fileSystem.IsDirectoryLink(InstalledPath(fileSystem)));
        // The whole point: what the project sees is the developer's working copy, uncommitted and all.
        Assert.Equal("# live edit", fileSystem.ReadAllText(fileSystem.CombinePath(InstalledPath(fileSystem), "thing.gd")));
    }

    [Fact]
    public async Task InstallAsync_WithALink_StillLocksTheUpstreamCommit()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);

        var locked = await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, new LocalLink("my-addon", SourceDirectory));

        // The lockfile is committed and must stay reproducible for everyone without the link (goal
        // #1). A machine-specific path in it would break every teammate.
        Assert.Equal(Sha, locked.Resolved);
        Assert.Equal(RepoUrl, locked.Git);
        Assert.DoesNotContain("elsewhere", locked.Path);
    }

    [Fact]
    public async Task InstallAsync_WithALink_DoesNotHashTheWorkingCopy()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);

        var locked = await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, new LocalLink("my-addon", SourceDirectory));

        // Recording a hash of in-progress edits would claim they are the expected content of an
        // upstream release, and every later install would then refuse over a mismatch.
        Assert.Null(locked.ContentHash);
    }

    [Fact]
    public async Task InstallAsync_LinkingOverAModifiedAddon_Refuses()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);
        var previous = await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false);

        fileSystem.WriteAllText(fileSystem.CombinePath(InstalledPath(fileSystem), "thing.gd"), "// my edits");

        var exception = await Assert.ThrowsAsync<SyncException>(() => installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: false, new LocalLink("my-addon", SourceDirectory)));

        Assert.Contains("local modifications", exception.Message);
    }

    [Fact]
    public async Task InstallAsync_LinkToAMissingFolder_SaysSo()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);

        var exception = await Assert.ThrowsAsync<SyncException>(() => installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, new LocalLink("my-addon", "gone/missing")));

        Assert.Contains("does not exist", exception.Message);
    }

    // -----------------------------------------------------------------------------------------
    // v0.16: a junction is a claim, and it has to agree with the declaration before anything writes
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task InstallAsync_OverALinkThisProjectNoLongerDeclares_RefusesAndChangesNothing()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);
        var previous = await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, new LocalLink("my-addon", SourceDirectory));

        // The junction survived while the declaration went away — a fresh clone dropped the
        // gitignored local file, or the link was made from a different project. Installing used to
        // delete the link and carry on; the version it wrote then landed inside the developer's own
        // checkout whenever anything routed past that unlink.
        var exception = await Assert.ThrowsAsync<SyncException>(() => installer.InstallAsync(
            Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: false));

        Assert.Contains("declares no link", exception.Message);
        Assert.Contains("dotquay link my-addon", exception.Message);
        Assert.Contains("--remove", exception.Message);

        // "Nothing was changed" has to be literally true, or the message is a lie at the worst moment.
        Assert.True(fileSystem.IsDirectoryLink(InstalledPath(fileSystem)));
        Assert.Equal("# live edit", fileSystem.ReadAllText(fileSystem.CombinePath(SourceDirectory, "thing.gd")));
        Assert.True(fileSystem.FileExists(fileSystem.CombinePath(SourceDirectory, "plugin.cfg")));
    }

    [Fact]
    public async Task InstallAsync_OverALinkThisProjectNoLongerDeclares_RefusesEvenUnderForce()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);
        var previous = await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, new LocalLink("my-addon", SourceDirectory));

        // --force means "discard local modifications in a folder dotquay owns" (P0-6). This folder is
        // not one, and nobody consented to anything about it by typing that flag.
        await Assert.ThrowsAsync<SyncException>(() => installer.InstallAsync(
            Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: true));

        Assert.True(fileSystem.IsDirectoryLink(InstalledPath(fileSystem)));
        Assert.Equal("# live edit", fileSystem.ReadAllText(fileSystem.CombinePath(SourceDirectory, "thing.gd")));
    }

    [Fact]
    public async Task InstallAsync_AfterTheLinkIsRemoved_InstallsARealCopyAndLeavesTheCheckoutAlone()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);
        var previous = await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, new LocalLink("my-addon", SourceDirectory));

        // What unlinking does (see DependencyWorkflow.RemoveAddonLink): the declaration goes, and the
        // link goes with it, so the reinstall has a folder dotquay owns to write into. Unlinking is
        // the consent — without it, the state above is indistinguishable from drift.
        fileSystem.DeleteDirectory(InstalledPath(fileSystem));

        await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: false);

        // Back on the commit the lockfile always named, with the checkout untouched.
        Assert.False(fileSystem.IsDirectoryLink(InstalledPath(fileSystem)));
        Assert.Equal("# committed version", fileSystem.ReadAllText(fileSystem.CombinePath(InstalledPath(fileSystem), "thing.gd")));
        Assert.Equal("# live edit", fileSystem.ReadAllText(fileSystem.CombinePath(SourceDirectory, "thing.gd")));
        Assert.True(fileSystem.FileExists(fileSystem.CombinePath(SourceDirectory, "plugin.cfg")));
    }

    [Fact]
    public async Task RemoveAddonLink_DropsTheDeclarationAndTheLinkButNotTheCheckout()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);
        await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, new LocalLink("my-addon", SourceDirectory));

        new LocalLinks(fileSystem).Set(ProjectDirectory, "my-addon", SourceDirectory);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), $"""
            [[addon]]
            name = "my-addon"
            git = "{RepoUrl}"
            resolved = "{Sha}"
            path = "."
            """);

        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), installer);

        Assert.True(workflow.RemoveAddonLink(ProjectDirectory, "my-addon"));

        Assert.Empty(new LocalLinks(fileSystem).Read(ProjectDirectory));
        Assert.False(fileSystem.DirectoryExists(InstalledPath(fileSystem)));
        Assert.Equal("# live edit", fileSystem.ReadAllText(fileSystem.CombinePath(SourceDirectory, "thing.gd")));
    }

    [Fact]
    public void RemoveAddonLink_WithAnUndeclaredJunction_IsStillTheWayOut()
    {
        var fileSystem = BuildFileSystemWithSource();
        fileSystem.CreateDirectoryLink(InstalledPath(fileSystem), SourceDirectory);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), $"""
            [[addon]]
            name = "my-addon"
            git = "{RepoUrl}"
            resolved = "{Sha}"
            path = "."
            """);

        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), BuildInstaller(fileSystem));

        // A junction with nothing declaring it is exactly the state an install refuses, and the
        // refusal names this command as one of its two ways out. If it answered "nothing to do" here
        // — which it did, until the published binary was run against a real junction and said so —
        // the advice would be impossible to follow in the one case it matters most.
        Assert.True(workflow.RemoveAddonLink(ProjectDirectory, "my-addon"));
        Assert.False(fileSystem.DirectoryExists(InstalledPath(fileSystem)));
        Assert.Equal("# live edit", fileSystem.ReadAllText(fileSystem.CombinePath(SourceDirectory, "thing.gd")));
    }

    [Fact]
    public void RemoveAddonLink_WithNoLinkAndNoDeclaration_ReportsNothingToDo()
    {
        var fileSystem = BuildFileSystemWithSource();
        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), BuildInstaller(fileSystem));

        Assert.False(workflow.RemoveAddonLink(ProjectDirectory, "my-addon"));
    }

    // -----------------------------------------------------------------------------------------
    // The destructive paths — the reason IFileSystem.DeleteDirectory must not follow links
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void DeleteDirectory_OnALink_RemovesOnlyTheLink()
    {
        var fileSystem = BuildFileSystemWithSource();
        fileSystem.CreateDirectoryLink(InstalledPath(fileSystem), SourceDirectory);

        fileSystem.DeleteDirectory(InstalledPath(fileSystem));

        Assert.False(fileSystem.DirectoryExists(InstalledPath(fileSystem)));
        // The source is a real checkout with uncommitted work in it. Recursing through the link would
        // be the only way dotquay can destroy something git cannot bring back.
        Assert.Equal("# live edit", fileSystem.ReadAllText(fileSystem.CombinePath(SourceDirectory, "thing.gd")));
        Assert.True(fileSystem.FileExists(fileSystem.CombinePath(SourceDirectory, "plugin.cfg")));
    }

    [Fact]
    public void DeleteDirectory_OnARealFolder_StillDeletesRecursively()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("real/nested/deep.txt", "x");

        fileSystem.DeleteDirectory("real");

        Assert.False(fileSystem.FileExists("real/nested/deep.txt"));
        Assert.False(fileSystem.DirectoryExists("real"));
    }

    [Fact]
    public async Task InstallAsync_RenamingALinkedAddon_RemovesTheOldLink()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);
        var link = new LocalLink("my-addon", SourceDirectory);
        var previous = await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, link);

        await installer.InstallAsync(Renamed("My-Addon-Folder"), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: false, link);

        Assert.True(fileSystem.IsDirectoryLink(fileSystem.CombinePath(ProjectDirectory, "addons", "My-Addon-Folder")));
        // Leaving it behind is not merely untidy: a second live link into the same checkout means Godot
        // loads the addon twice under two names, and the folder the rename was meant to vacate keeps
        // satisfying every res:// path it was meant to redirect.
        Assert.False(fileSystem.DirectoryExists(InstalledPath(fileSystem)));
    }

    [Fact]
    public async Task InstallAsync_RenamingALinkedAddon_LeavesTheSourceUntouched()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);
        var link = new LocalLink("my-addon", SourceDirectory);
        var previous = await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, link);

        await installer.InstallAsync(Renamed("My-Addon-Folder"), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: false, link);

        // The rename retires a link, and retiring a link must never reach through it. This is the same
        // invariant orphan pruning and `remove` depend on, on the one path that used to skip it.
        Assert.Equal("# live edit", fileSystem.ReadAllText(fileSystem.CombinePath(SourceDirectory, "thing.gd")));
        Assert.True(fileSystem.FileExists(fileSystem.CombinePath(SourceDirectory, "plugin.cfg")));
    }

    [Fact]
    public async Task InstallAsync_RenamingALinkedAddon_DoesNotCallTheCheckoutHandEdits()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);
        var link = new LocalLink("my-addon", SourceDirectory);
        var previous = await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, link);

        // Uncommitted work in the developer's checkout — the normal state of a linked addon.
        fileSystem.WriteAllText(fileSystem.CombinePath(SourceDirectory, "thing.gd"), "# half-finished");

        // The rename guard hashes the folder it is about to delete. Through a link that folder is the
        // developer's working copy, so hashing it refuses the rename over "hand-edits" to files dotquay
        // never installed — and tells them to capture their own source as a patch.
        await installer.InstallAsync(Renamed("My-Addon-Folder"), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: false, link);

        Assert.Equal("# half-finished", fileSystem.ReadAllText(fileSystem.CombinePath(SourceDirectory, "thing.gd")));
    }

    [Fact]
    public async Task InstallAsync_UnlinkingAndRenamingAtOnce_RemovesTheOldLink()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);
        var previous = await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, new LocalLink("my-addon", SourceDirectory));

        await installer.InstallAsync(Renamed("My-Addon-Folder"), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: false);

        // Dropping the link and changing install_as in one go: the new folder is a real installed copy,
        // and the old link goes away without the checkout going with it.
        Assert.False(fileSystem.DirectoryExists(InstalledPath(fileSystem)));
        Assert.Equal(
            "# committed version",
            fileSystem.ReadAllText(fileSystem.CombinePath(ProjectDirectory, "addons", "My-Addon-Folder", "thing.gd")));
        Assert.Equal("# live edit", fileSystem.ReadAllText(fileSystem.CombinePath(SourceDirectory, "thing.gd")));
    }

    [Fact]
    public async Task RemoveAsync_ALinkedAddon_LeavesTheSourceUntouched()
    {
        var fileSystem = BuildFileSystemWithSource();
        var installer = BuildInstaller(fileSystem);
        await installer.InstallAsync(Resolved(), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false, new LocalLink("my-addon", SourceDirectory));

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), $"""
            [addons.my-addon]
            git = "{RepoUrl}"
            rev = "{Sha}"
            path = "."
            """);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), $"""
            [[addon]]
            name = "my-addon"
            git = "{RepoUrl}"
            resolved = "{Sha}"
            path = "."
            """);

        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), installer);
        await workflow.RemoveAsync(ProjectDirectory, "my-addon");

        Assert.False(fileSystem.DirectoryExists(InstalledPath(fileSystem)));
        Assert.Equal("# live edit", fileSystem.ReadAllText(fileSystem.CombinePath(SourceDirectory, "thing.gd")));
    }

    // -----------------------------------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_ALinkedAddon_ReportsLinked()
    {
        var fileSystem = BuildFileSystemWithSource();
        fileSystem.CreateDirectoryLink(InstalledPath(fileSystem), SourceDirectory);

        var locked = new LockedAddon
        {
            Name = "my-addon", Git = RepoUrl, Resolved = Sha, Path = ".", ContentHash = "sha256:whatever",
        };

        // Not "modified": a link's contents are a live working copy, so it would never match the
        // lockfile and every linked addon would sit permanently in a state that reads like a problem.
        Assert.Equal(AddonStatusKind.Linked, AddonStatusEvaluator.Evaluate(fileSystem, ProjectDirectory, locked));
    }
}
