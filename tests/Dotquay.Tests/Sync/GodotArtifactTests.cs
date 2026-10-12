using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

/// <summary>
/// Covers dotquay's handling of the files the Godot editor writes into the folders dotquay manages
/// (SPEC.md §7.3, §9): the <c>.uid</c> beside every script since Godot 4.4, and the <c>.import</c>
/// beside every asset.
/// </summary>
/// <remarks>
/// One file for the whole behaviour rather than a case spread across the hasher's, the dirty
/// checker's, and the sync engine's suites, because the three only make sense together: the hash
/// ignores these files, the dirty check therefore stops reporting untouched addons as modified, and
/// the swap puts back the ones the incoming version doesn't ship so UIDs stay stable.
/// </remarks>
public class GodotArtifactTests
{
    // -- the rule itself --------------------------------------------------------------------

    [Theory]
    [InlineData("GodotFrameProvider.cs.uid")]
    [InlineData("scripts/gut.gd.uid")]
    [InlineData("icon.png.import")]
    [InlineData("images/Folder.svg.import")]
    [InlineData(".godot/uid_cache.bin")]
    [InlineData("nested/.godot/anything")]
    [InlineData(".import/legacy-godot-3-cache")]
    // The editor's hot-reload copy of a GDExtension library, beside the original.
    [InlineData("libs/x86_64/~GDMP.windows.dll")]
    [InlineData("~libGDMP.linux.so")]
    [InlineData(@"libs\x86_64\~GDMP.windows.dll")]
    public void IsArtifact_EditorGeneratedPaths_AreRecognised(string relativePath) =>
        Assert.True(GodotArtifacts.IsArtifact(relativePath));

    [Theory]
    [InlineData("plugin.cfg")]
    [InlineData("scripts/gut.gd")]
    [InlineData("icon.png")]
    [InlineData("README.md")]
    // The addon's own content, not an artifact, despite the substring.
    [InlineData("uid.gd")]
    [InlineData("import.gd")]
    // Segment-wise matching: a real folder whose name merely starts with a reserved one.
    [InlineData("my.godot-stuff/thing.gd")]
    [InlineData(".godotignore")]
    // A tilde elsewhere in the name, or on a folder, is not the editor's copy marker.
    [InlineData("libs/GDMP~backup.dll")]
    [InlineData("~tmp/thing.gd")]
    public void IsArtifact_AddonContent_IsNot(string relativePath) =>
        Assert.False(GodotArtifacts.IsArtifact(relativePath));

    [Fact]
    public void IsArtifact_AcceptsWindowsSeparators() =>
        Assert.True(GodotArtifacts.IsArtifact(@"images\Folder.svg.import"));

    // -- the editor's native-library copies -------------------------------------------------

    [Fact]
    public void HashDirectory_IsUnchangedByTheEditorLoadingAGdExtension()
    {
        // The bug, reduced: the editor loads GDMP.windows.dll by copying it to ~GDMP.windows.dll and
        // loading the copy, so an untouched addon grew a 13 MB file and read as modified.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/GDMP/libs/x86_64/GDMP.windows.dll", "native library");

        var atInstall = ContentHasher.HashDirectory(fileSystem, "addons/GDMP");

        fileSystem.WriteAllText("addons/GDMP/libs/x86_64/~GDMP.windows.dll", "native library");

        Assert.Equal(atInstall, ContentHasher.HashDirectory(fileSystem, "addons/GDMP"));
    }

    [Fact]
    public void SwapAndHash_DoesNotCarryOverTheEditorsLibraryCopy()
    {
        // Unlike a .uid it carries no identity, and after the swap it would be a stale copy of the
        // library that was just replaced.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/GDMP/libs/GDMP.windows.dll", "v1");
        fileSystem.WriteAllText("addons/GDMP/libs/~GDMP.windows.dll", "v1");
        var installed = ContentHasher.HashDirectory(fileSystem, "addons/GDMP");

        fileSystem.WriteAllText("staging/libs/GDMP.windows.dll", "v2");

        new SyncEngine(fileSystem).SwapAndHash("staging", "addons/GDMP", installed, force: false);

        Assert.Equal("v2", fileSystem.ReadAllText("addons/GDMP/libs/GDMP.windows.dll"));
        Assert.False(fileSystem.FileExists("addons/GDMP/libs/~GDMP.windows.dll"));
    }

    // -- the hash ---------------------------------------------------------------------------

    [Fact]
    public void HashDirectory_IsUnchangedByTheEditorImportingTheProject()
    {
        // The bug, reduced: an addon installs clean, Godot opens the project, and every script grows
        // a .uid sidecar. Nothing the user did, but the folder used to hash differently afterwards.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/r3/GodotFrameProvider.cs", "class GodotFrameProvider {}");

        var atInstall = ContentHasher.HashDirectory(fileSystem, "addons/r3");

        fileSystem.WriteAllText("addons/r3/GodotFrameProvider.cs.uid", "uid://bxwpwbdv4fm02");
        fileSystem.WriteAllText("addons/r3/icon.png.import", "[remap]\nuid=\"uid://cabc\"");

        Assert.Equal(atInstall, ContentHasher.HashDirectory(fileSystem, "addons/r3"));
    }

    [Fact]
    public void HashDirectory_StillNoticesARealEdit()
    {
        // The guard has to keep working — ignoring artifacts must not become ignoring changes.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/r3/GodotFrameProvider.cs", "original");

        var before = ContentHasher.HashDirectory(fileSystem, "addons/r3");
        fileSystem.WriteAllText("addons/r3/GodotFrameProvider.cs", "hand-edited");

        Assert.NotEqual(before, ContentHasher.HashDirectory(fileSystem, "addons/r3"));
    }

    [Fact]
    public void HashDirectoryIncludingArtifacts_StillSeesThem()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/r3/GodotFrameProvider.cs", "class GodotFrameProvider {}");

        var before = ContentHasher.HashDirectoryIncludingArtifacts(fileSystem, "addons/r3");
        fileSystem.WriteAllText("addons/r3/GodotFrameProvider.cs.uid", "uid://bxwpwbdv4fm02");

        Assert.NotEqual(before, ContentHasher.HashDirectoryIncludingArtifacts(fileSystem, "addons/r3"));
    }

    // -- the migration ----------------------------------------------------------------------

    [Fact]
    public void Check_LockfileHashFromBeforeTheChange_IsStillClean()
    {
        // An addon that ships its own .uid files had them folded into the hash recorded at install
        // time. Excluding them now changes that number — on content nobody touched — so the old form
        // has to keep counting as clean.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/gut/gut.gd", "extends Node");
        fileSystem.WriteAllText("addons/gut/gut.gd.uid", "uid://shipped-by-the-addon");

        var legacyHash = ContentHasher.HashDirectoryIncludingArtifacts(fileSystem, "addons/gut");

        var result = DirtyChecker.Check(fileSystem, "addons/gut", legacyHash);

        Assert.False(result.IsDirty);

        // ...and the caller records the current form, so the addon migrates on its next install.
        Assert.Equal(ContentHasher.HashDirectory(fileSystem, "addons/gut"), result.ActualContentHash);
    }

    [Fact]
    public void Check_LegacyHashDoesNotExcuseARealEdit()
    {
        // Accepting either form must not become accepting anything.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/gut/gut.gd", "extends Node");
        var legacyHash = ContentHasher.HashDirectoryIncludingArtifacts(fileSystem, "addons/gut");

        fileSystem.WriteAllText("addons/gut/gut.gd", "extends Node # mine");

        Assert.True(DirtyChecker.Check(fileSystem, "addons/gut", legacyHash).IsDirty);
    }

    [Fact]
    public void SwapAndHash_RecordedHashReconcilesWithNothing_ButTheTreeIsPristine_IsAllowed()
    {
        // The real case, from a real project. Gut ships its own .uid files, so they were folded into
        // the hash recorded at install; Godot then rewrote its .import files. Neither the current nor
        // the legacy hash can match any more, on content byte-identical to upstream. Comparing
        // numbers cannot resolve that, so the swap compares trees instead.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/gut/gut.gd", "extends Node");
        fileSystem.WriteAllText("addons/gut/gut.gd.uid", "uid://shipped-by-the-addon");
        fileSystem.WriteAllText("addons/gut/icon.png.import", "[remap]\nuid=\"uid://rewritten-by-godot\"");

        fileSystem.WriteAllText("staging/gut.gd", "extends Node");
        fileSystem.WriteAllText("staging/gut.gd.uid", "uid://shipped-by-the-addon");
        fileSystem.WriteAllText("staging/icon.png.import", "[remap]\nuid=\"uid://as-published\"");

        var unreconcilable = "sha256:whatever-was-recorded-at-install-time";

        var hash = new SyncEngine(fileSystem).SwapAndHash("staging", "addons/gut", unreconcilable, force: false);

        // Allowed without --force, and the returned hash re-baselines the lockfile so the addon
        // self-heals rather than needing this acquittal again.
        Assert.Equal(ContentHasher.HashDirectory(fileSystem, "addons/gut"), hash);
    }

    [Fact]
    public void SwapAndHash_TreeDiffersFromIncoming_StillRefuses()
    {
        // The acquittal must stay narrow: it excuses only a destination identical to what would
        // replace it. A genuine hand-edit is exactly the case P0-6 exists for.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/gut/gut.gd", "extends Node # my edit");
        fileSystem.WriteAllText("staging/gut.gd", "extends Node");

        Assert.Throws<SyncException>(() =>
            new SyncEngine(fileSystem).SwapAndHash("staging", "addons/gut", "sha256:stale", force: false));

        Assert.Equal("extends Node # my edit", fileSystem.ReadAllText("addons/gut/gut.gd"));
    }

    // -- the swap ---------------------------------------------------------------------------

    [Fact]
    public void SwapAndHash_CarriesOverGeneratedArtifactsTheNewVersionDoesNotShip()
    {
        // Without this the update deletes the .uid, Godot generates a *new* one, and every uid://
        // reference into the addon points at something that no longer exists.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/r3/GodotFrameProvider.cs", "v1");
        fileSystem.WriteAllText("addons/r3/GodotFrameProvider.cs.uid", "uid://generated-locally");
        var installed = ContentHasher.HashDirectory(fileSystem, "addons/r3");

        fileSystem.WriteAllText("staging/GodotFrameProvider.cs", "v2");

        new SyncEngine(fileSystem).SwapAndHash("staging", "addons/r3", installed, force: false);

        Assert.Equal("v2", fileSystem.ReadAllText("addons/r3/GodotFrameProvider.cs"));
        Assert.Equal("uid://generated-locally", fileSystem.ReadAllText("addons/r3/GodotFrameProvider.cs.uid"));
    }

    [Fact]
    public void SwapAndHash_AnArtifactTheAddonShipsWins()
    {
        // The author versioned it, and the author's uid is what the addon's own scenes reference.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/gut/gut.gd", "v1");
        fileSystem.WriteAllText("addons/gut/gut.gd.uid", "uid://generated-locally");
        var installed = ContentHasher.HashDirectory(fileSystem, "addons/gut");

        fileSystem.WriteAllText("staging/gut.gd", "v2");
        fileSystem.WriteAllText("staging/gut.gd.uid", "uid://shipped-by-the-addon");

        new SyncEngine(fileSystem).SwapAndHash("staging", "addons/gut", installed, force: false);

        Assert.Equal("uid://shipped-by-the-addon", fileSystem.ReadAllText("addons/gut/gut.gd.uid"));
    }

    [Fact]
    public void SwapAndHash_CarriesArtifactsFromNestedFolders()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/gut/images/icon.png", "binary");
        fileSystem.WriteAllText("addons/gut/images/icon.png.import", "[remap]\nuid=\"uid://cabc\"");
        var installed = ContentHasher.HashDirectory(fileSystem, "addons/gut");

        fileSystem.WriteAllText("staging/images/icon.png", "binary v2");

        new SyncEngine(fileSystem).SwapAndHash("staging", "addons/gut", installed, force: false);

        Assert.True(fileSystem.FileExists("addons/gut/images/icon.png.import"));
    }

    [Fact]
    public void SwapAndHash_DoesNotCarryOverRealFiles()
    {
        // A file the new version dropped must actually disappear; only artifacts are preserved.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/gut/gut.gd", "v1");
        fileSystem.WriteAllText("addons/gut/removed_upstream.gd", "gone in v2");
        var installed = ContentHasher.HashDirectory(fileSystem, "addons/gut");

        fileSystem.WriteAllText("staging/gut.gd", "v2");

        new SyncEngine(fileSystem).SwapAndHash("staging", "addons/gut", installed, force: false);

        Assert.False(fileSystem.FileExists("addons/gut/removed_upstream.gd"));
    }

    [Fact]
    public void SwapAndHash_ReturnedHashIgnoresTheCarriedArtifacts()
    {
        // The hash recorded in the lockfile must describe the addon, not this machine's uids —
        // otherwise a teammate installing the same commit records a different number.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/r3/frame.cs", "v1");
        fileSystem.WriteAllText("addons/r3/frame.cs.uid", "uid://this-machine-only");
        var installed = ContentHasher.HashDirectory(fileSystem, "addons/r3");

        fileSystem.WriteAllText("staging/frame.cs", "v2");

        var hash = new SyncEngine(fileSystem).SwapAndHash("staging", "addons/r3", installed, force: false);

        var clean = new InMemoryFileSystem();
        clean.WriteAllText("addons/r3/frame.cs", "v2");

        Assert.Equal(ContentHasher.HashDirectory(clean, "addons/r3"), hash);
    }
}
