using Dotquay.Core.Doctor;
using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

/// <summary>
/// Covers the folder-casing problem, from both ends: correcting it at install time, and reporting it
/// in projects that already have it.
/// </summary>
/// <remarks>
/// The worked example throughout is Gut, because it is where this was found. The repository is
/// <c>bitwes/Gut</c>, so dotquay's <c>addons/&lt;name&gt;</c> guess became <c>addons/Gut</c> — but the
/// subtree in the repository is <c>addons/gut</c>, and 88 of Gut's own files say
/// <c>res://addons/gut/</c>. Windows finds the files anyway, so the addon "worked" while Godot
/// silently rewrote every reference on each open and the addon never once read as clean.
/// </remarks>
public class FolderCaseTests {
    // -- resolving the subtree's real casing --------------------------------------------------

    [Fact]
    public void ResolveSubtreePath_CorrectsCaseAgainstTheRepository() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("cache/repo/addons/gut/plugin.cfg", "cfg");

        Assert.Equal("addons/gut", SubtreeExtractor.ResolveSubtreePath(fileSystem, "cache/repo", "addons/Gut"));
    }

    [Fact]
    public void ResolveSubtreePath_AlreadyCorrect_IsUnchanged() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("cache/repo/addons/dialogue_manager/plugin.cfg", "cfg");

        Assert.Equal(
            "addons/dialogue_manager",
            SubtreeExtractor.ResolveSubtreePath(fileSystem, "cache/repo", "addons/dialogue_manager"));
    }

    [Fact]
    public void ResolveSubtreePath_NoCaseInsensitiveMatch_IsLeftAlone() {
        // A genuinely different name is a layout decision, not a typo. Leaving it untouched keeps the
        // existing "path does not exist in the fetched repository" error as the thing that fires.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("cache/repo/addons/dialogue_manager/plugin.cfg", "cfg");

        Assert.Equal(
            "addons/something_else",
            SubtreeExtractor.ResolveSubtreePath(fileSystem, "cache/repo", "addons/something_else"));
    }

    [Fact]
    public void ResolveSubtreePath_RepoRoot_IsUnchanged() =>
        Assert.Equal(".", SubtreeExtractor.ResolveSubtreePath(new InMemoryFileSystem(), "cache/repo", "."));

    [Fact]
    public void ResolveSubtreePath_CorrectsEverySegment() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("cache/repo/src/R3.Godot/addons/R3.Godot/plugin.cfg", "cfg");

        Assert.Equal(
            "src/R3.Godot/addons/R3.Godot",
            SubtreeExtractor.ResolveSubtreePath(fileSystem, "cache/repo", "SRC/r3.godot/ADDONS/r3.godot"));
    }

    // -- what doctor reports ------------------------------------------------------------------

    [Fact]
    public void Check_AddonReferencingADifferentlyCasedFolder_IsReported() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/addons/Gut/gut.gd", "const X = preload(\"res://addons/gut/utils.gd\")");
        fileSystem.WriteAllText(
            "project/addons/Gut/gui/theme.tres",
            "[ext_resource path=\"res://addons/gut/fonts/Anonymous.ttf\"]");

        var mismatch = AddonFolderCaseChecker.Check(fileSystem, "project/addons/Gut", "Gut");

        Assert.NotNull(mismatch);
        Assert.Equal("Gut", mismatch!.Folder);
        Assert.Equal("gut", mismatch.Expected);
        Assert.Equal(2, mismatch.ReferenceCount);
    }

    [Fact]
    public void Check_CasingAgrees_IsNotReported() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/addons/gut/gut.gd", "preload(\"res://addons/gut/utils.gd\")");

        Assert.Null(AddonFolderCaseChecker.Check(fileSystem, "project/addons/gut", "gut"));
    }

    [Fact]
    public void Check_ReferenceToAnotherAddon_IsNotReported() {
        // An addon depending on a different addon is normal. Reporting those would bury the one
        // finding that matters under every legitimate cross-addon reference in the project.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/addons/gut/gut.gd", "preload(\"res://addons/some_other_addon/api.gd\")");

        Assert.Null(AddonFolderCaseChecker.Check(fileSystem, "project/addons/gut", "gut"));
    }

    [Fact]
    public void Check_BinaryFiles_AreNotScanned() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/addons/Gut/icon.png", "res://addons/gut/ appearing in binary noise");

        Assert.Null(AddonFolderCaseChecker.Check(fileSystem, "project/addons/Gut", "Gut"));
    }

    [Fact]
    public void Check_MostReferencedSpellingWins() {
        // One stray typo upstream shouldn't outvote the name the addon actually uses.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/addons/GUT/a.gd", "preload(\"res://addons/gut/x.gd\")");
        fileSystem.WriteAllText("project/addons/GUT/b.gd", "preload(\"res://addons/gut/y.gd\")");
        fileSystem.WriteAllText("project/addons/GUT/c.gd", "preload(\"res://addons/Gut/z.gd\")");

        Assert.Equal("gut", AddonFolderCaseChecker.Check(fileSystem, "project/addons/GUT", "GUT")!.Expected);
    }

    [Fact]
    public void Check_MissingFolder_IsNotAFault() =>
        Assert.Null(AddonFolderCaseChecker.Check(new InMemoryFileSystem(), "project/addons/gone", "gone"));
}
