using Dotquay.Core.Abstractions;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

/// <summary>
/// Covers <c>install_as</c> (SPEC.md §7.1) — installing an addon under a folder name that isn't its
/// own.
/// </summary>
/// <remarks>
/// The motivating case: the VRM addon does
/// <c>preload("res://addons/Godot-MToon-Shader/mtoon.gdshader")</c>. Godot resolves <c>preload</c> at
/// parse time, so the shader addon landing in <c>addons/mtoon-shader</c> doesn't degrade anything at
/// runtime — VRM fails to compile outright. A Godot addon's folder name is frequently part of its
/// public contract rather than a preference, and it routinely differs from both the repo name and the
/// manifest key.
/// </remarks>
public class InstallAsTests {
    private const string ProjectDirectory = "project";
    private const string RepoUrl = "https://example.com/mtoon-shader.git";

    private static string Sha => new('a', 40);

    private static (AddonInstaller Installer, InMemoryFileSystem FileSystem) Build() {
        var fileSystem = new InMemoryFileSystem();
        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[RepoUrl] = new Dictionary<string, string> {
            ["mtoon.gdshader"] = "shader_type spatial;",
            ["plugin.cfg"] = "[plugin]\nname=\"MToon\"\nversion=\"1.0.0\"\nscript=\"x.gd\"",
        };

        var cache = new RepoCache(fileSystem, gitClient, "cache");
        var installer = new AddonInstaller(fileSystem, cache, new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        return (installer, fileSystem);
    }

    /// <summary>The repo root is the addon (<c>path = "."</c>), as it is for the real mtoon-shader.</summary>
    private static ResolvedAddon Resolved(string? installAs) =>
        new("mtoon-shader", RepoUrl, Sha, DisplayVersion: null, Path: ".", InstallAs: installAs);

    [Fact]
    public async Task InstallAsync_WithoutInstallAs_UsesTheAddonName() {
        var (installer, fileSystem) = Build();

        await installer.InstallAsync(Resolved(installAs: null), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false);

        // The behaviour every existing project depends on — adding the field must not move anything.
        Assert.True(fileSystem.FileExists(fileSystem.CombinePath(ProjectDirectory, "addons", "mtoon-shader", "mtoon.gdshader")));
    }

    [Fact]
    public async Task InstallAsync_WithInstallAs_UsesThatFolder() {
        var (installer, fileSystem) = Build();

        await installer.InstallAsync(Resolved("Godot-MToon-Shader"), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false);

        Assert.True(fileSystem.FileExists(
            fileSystem.CombinePath(ProjectDirectory, "addons", "Godot-MToon-Shader", "mtoon.gdshader")));
        Assert.False(fileSystem.DirectoryExists(fileSystem.CombinePath(ProjectDirectory, "addons", "mtoon-shader")));
    }

    [Fact]
    public async Task InstallAsync_RecordsTheFolderInTheLockfile() {
        var (installer, _) = Build();

        var locked = await installer.InstallAsync(Resolved("Godot-MToon-Shader"), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false);

        // `install` replays from the lockfile alone (SPEC.md §5) and must find the same folder — this
        // is also the only record of where a *transitive* addon went, since it's in no manifest.
        Assert.Equal("Godot-MToon-Shader", locked.Folder);
        Assert.Equal("Godot-MToon-Shader", locked.EffectiveFolder);
        Assert.Equal("mtoon-shader", locked.Name);
    }

    [Fact]
    public async Task InstallAsync_KeepsNameAndFolderDistinct() {
        var (installer, _) = Build();

        var locked = await installer.InstallAsync(Resolved("Godot-MToon-Shader"), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false);

        // Identity stays the name: unification, dependency edges, and `remove` all key on it, so an
        // addon installed under an alias is still one addon, not two.
        Assert.NotEqual(locked.Name, locked.EffectiveFolder);
        Assert.Equal(".", locked.Path);
    }

    [Fact]
    public async Task InstallAsync_ChangingTheFolder_MovesTheAddon() {
        var (installer, fileSystem) = Build();
        var previous = await installer.InstallAsync(Resolved(installAs: null), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false);

        await installer.InstallAsync(Resolved("Godot-MToon-Shader"), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: false);

        Assert.True(fileSystem.DirectoryExists(fileSystem.CombinePath(ProjectDirectory, "addons", "Godot-MToon-Shader")));
        // The old folder must go. Godot loads whatever is under addons/, so a leftover copy keeps
        // satisfying the very preload paths the rename existed to redirect — the move would look
        // successful while the stale code is what actually runs.
        Assert.False(fileSystem.DirectoryExists(fileSystem.CombinePath(ProjectDirectory, "addons", "mtoon-shader")));
    }

    [Fact]
    public async Task InstallAsync_ChangingTheFolder_RefusesToDiscardHandEdits() {
        var (installer, fileSystem) = Build();
        var previous = await installer.InstallAsync(Resolved(installAs: null), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false);

        // A hand-edit in the folder the rename is about to delete.
        fileSystem.WriteAllText(
            fileSystem.CombinePath(ProjectDirectory, "addons", "mtoon-shader", "mtoon.gdshader"), "// my changes");

        var exception = await Assert.ThrowsAsync<ModifiedAddonException>(
            () => installer.InstallAsync(Resolved("Godot-MToon-Shader"), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: false));

        // Without this guard, renaming would be the one way to make dotquay destroy hand-edits without
        // asking: the swap's own dirty check looks at the *new* folder, which doesn't exist yet and is
        // therefore trivially clean (requirement P0-6).
        Assert.Contains("local modifications", exception.Message);
        Assert.Contains("dotquay patch mtoon-shader", exception.Message);
    }

    [Fact]
    public async Task InstallAsync_ChangingTheFolder_LeavesEverythingAloneWhenItRefuses() {
        var (installer, fileSystem) = Build();
        var previous = await installer.InstallAsync(Resolved(installAs: null), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false);
        fileSystem.WriteAllText(
            fileSystem.CombinePath(ProjectDirectory, "addons", "mtoon-shader", "mtoon.gdshader"), "// my changes");

        await Assert.ThrowsAsync<ModifiedAddonException>(
            () => installer.InstallAsync(Resolved("Godot-MToon-Shader"), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: false));

        // Checked before anything is written, so a refusal is a no-op rather than a half-done move.
        Assert.True(fileSystem.DirectoryExists(fileSystem.CombinePath(ProjectDirectory, "addons", "mtoon-shader")));
        Assert.False(fileSystem.DirectoryExists(fileSystem.CombinePath(ProjectDirectory, "addons", "Godot-MToon-Shader")));
        Assert.Equal(
            "// my changes",
            fileSystem.ReadAllText(fileSystem.CombinePath(ProjectDirectory, "addons", "mtoon-shader", "mtoon.gdshader")));
    }

    [Fact]
    public async Task InstallAsync_ChangingTheFolder_WithForce_DiscardsThem() {
        var (installer, fileSystem) = Build();
        var previous = await installer.InstallAsync(Resolved(installAs: null), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false);
        fileSystem.WriteAllText(
            fileSystem.CombinePath(ProjectDirectory, "addons", "mtoon-shader", "mtoon.gdshader"), "// my changes");

        await installer.InstallAsync(Resolved("Godot-MToon-Shader"), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: true);

        Assert.False(fileSystem.DirectoryExists(fileSystem.CombinePath(ProjectDirectory, "addons", "mtoon-shader")));
        Assert.True(fileSystem.DirectoryExists(fileSystem.CombinePath(ProjectDirectory, "addons", "Godot-MToon-Shader")));
    }

    [Fact]
    public async Task InstallAsync_ReinstallingIntoTheSameFolder_IsNotTreatedAsARename() {
        var (installer, fileSystem) = Build();
        var previous = await installer.InstallAsync(Resolved("Godot-MToon-Shader"), ProjectLayout.WithoutPatches(ProjectDirectory), previous: null, force: false);

        await installer.InstallAsync(Resolved("Godot-MToon-Shader"), ProjectLayout.WithoutPatches(ProjectDirectory), previous, force: false);

        // The rename branch deletes the old folder; if it fired here it would delete the addon it
        // just installed, since old and new are the same directory.
        Assert.True(fileSystem.FileExists(
            fileSystem.CombinePath(ProjectDirectory, "addons", "Godot-MToon-Shader", "mtoon.gdshader")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../../etc")]
    [InlineData("nested/folder")]
    [InlineData("C:\\Windows")]
    [InlineData("")]
    public void ManifestReader_RejectsAnUnsafeInstallAs(string folder) {
        var text = $"""
            [addons.evil]
            git = "https://example.com/evil.git"
            version = "~1.0"
            install_as = "{folder.Replace("\\", "\\\\")}"
            """;

        // This is a safety boundary, not tidiness: the sync engine deletes and replaces the
        // destination wholesale (SPEC.md §9 step 5), and manifests are read from third-party
        // repositories nobody in the chain necessarily audited.
        var exception = Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject(text));
        Assert.Contains("plain folder name", exception.Message);
    }

    [Fact]
    public void ManifestReader_AcceptsAnOrdinaryFolderName() {
        var manifest = ManifestReader.ReadProject("""
            [addons.mtoon-shader]
            git = "https://example.com/mtoon-shader.git"
            version = "~1.0"
            install_as = "Godot-MToon-Shader"
            """);

        Assert.Equal("Godot-MToon-Shader", manifest.Addons["mtoon-shader"].InstallAs);
        Assert.Equal("Godot-MToon-Shader", manifest.Addons["mtoon-shader"].EffectiveFolder);
    }

    [Fact]
    public void AddonSpec_FolderDefaultsToTheName() {
        var spec = new AddonSpec { Name = "mtoon-shader", Git = RepoUrl, Version = "~1.0" };

        Assert.Equal("mtoon-shader", spec.EffectiveFolder);
    }

    [Fact]
    public void Manifest_RoundTripsInstallAs() {
        var original = ManifestReader.ReadProject("""
            [addons.mtoon-shader]
            git = "https://example.com/mtoon-shader.git"
            version = "~1.0"
            install_as = "Godot-MToon-Shader"
            """);

        var reparsed = ManifestReader.ReadProject(ManifestWriter.Serialize(original));

        Assert.Equal("Godot-MToon-Shader", reparsed.Addons["mtoon-shader"].InstallAs);
    }

    [Fact]
    public void Lockfile_RoundTripsTheFolder() {
        var lockfile = new DotquayLockfile {
            Addons =
            [
                new LockedAddon
                {
                    Name = "mtoon-shader", Git = RepoUrl, Resolved = Sha, Path = ".", Folder = "Godot-MToon-Shader",
                },
            ],
        };

        var reparsed = LockfileReader.Read(LockfileWriter.Serialize(lockfile));

        Assert.Equal("Godot-MToon-Shader", reparsed.Addons[0].Folder);
    }

    [Fact]
    public void Lockfile_OmitsTheFolderWhenItMatchesTheName() {
        var lockfile = new DotquayLockfile {
            Addons = [new LockedAddon { Name = "leaf", Git = RepoUrl, Resolved = Sha, Path = "addons/leaf", Folder = "leaf" }],
        };

        // Adding this field must not churn the lockfile of every project that never needed it.
        Assert.DoesNotContain("folder =", LockfileWriter.Serialize(lockfile));
    }

    [Fact]
    public void LockedAddon_CopyingWithAChange_KeepsTheFolder() {
        var locked = new LockedAddon {
            Name = "mtoon-shader",
            Git = RepoUrl,
            Resolved = Sha,
            Path = ".",
            Folder = "Godot-MToon-Shader",
        };

        var copied = locked with { Dependencies = ["other"] };

        // LockedAddon is a record precisely so this can't be got wrong. It was a class, and the two
        // hand-written copy helpers that replaced `with` both silently dropped Folder — the addon
        // installed into the right folder, and every later command looked in the wrong one.
        Assert.Equal("Godot-MToon-Shader", copied.Folder);
        Assert.Equal(Sha, copied.Resolved);
        Assert.Equal(["other"], copied.Dependencies);
    }

    [Fact]
    public void Lockfile_WrittenBeforeThisFeature_StillReadsCorrectly() {
        var parsed = LockfileReader.Read($"""
            [[addon]]
            name = "leaf"
            git = "{RepoUrl}"
            resolved = "{Sha}"
            path = "addons/leaf"
            """);

        Assert.Null(parsed.Addons[0].Folder);
        Assert.Equal("leaf", parsed.Addons[0].EffectiveFolder);
    }
}
