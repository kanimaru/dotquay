using Dotquay.Core.Doctor;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <see cref="GodotProjectRootChecker"/> on its own: the structural test that tells a Godot
/// project root apart from an addon, and the guess at where the real addon went.
/// </summary>
/// <remarks>
/// Pinned by a real incident (2026-09-18): vrm-addon moved its plugin from the repository root to
/// <c>addons/vrm</c>, vbeami's package manifest kept saying <c>path = "."</c>, and every consumer got
/// the whole vrm-addon repository installed under <c>addons/vrm</c> with nothing reporting why.
/// </remarks>
public class GodotProjectRootCheckerTests {
    private const string Tree = "staging/vrm";

    private static InMemoryFileSystem TreeWith(params string[] files) {
        var fileSystem = new InMemoryFileSystem();
        foreach (var file in files) {
            fileSystem.WriteAllText(fileSystem.CombinePath(Tree, file), "x");
        }

        return fileSystem;
    }

    // -----------------------------------------------------------------------------------------
    // Telling a project from an addon
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Check_ProjectRootWithTheAddonNestedInside_IsReported() {
        var fileSystem = TreeWith("project.godot", "addons/vrm/plugin.cfg", "addons/vrm/vrm_plugin.gd");

        var finding = GodotProjectRootChecker.Check(fileSystem, Tree, "vrm");

        Assert.NotNull(finding);
        Assert.Equal("addons/vrm", finding.SuggestedPath);
        Assert.Equal(new[] { "addons/vrm" }, finding.Candidates);
    }

    [Fact]
    public void Check_AddonWithAPluginCfgAtItsRoot_IsNotAProject() {
        // The ordinary shape of every installed addon — nothing to say, however many other files
        // it ships.
        var fileSystem = TreeWith("plugin.cfg", "vrm_plugin.gd", "addons/gut/plugin.cfg");

        Assert.Null(GodotProjectRootChecker.Check(fileSystem, Tree, "vrm"));
    }

    [Fact]
    public void Check_AddonShippingItsOwnProjectGodotBesidePluginCfg_IsStillAnAddon() {
        // Some addons keep a project.godot at their own root so the folder opens as a demo. Godot
        // treats it as a plugin because plugin.cfg is there, and so does this check.
        var fileSystem = TreeWith("plugin.cfg", "project.godot", "demo.tscn");

        Assert.Null(GodotProjectRootChecker.Check(fileSystem, Tree, "vrm"));
    }

    [Fact]
    public void Check_PlainContentFolderWithNeitherMarker_IsLeftAlone() {
        // A shader pack or asset bundle has no plugin.cfg and no project.godot. Nothing here says it
        // is a project, so nothing is reported — this check must not become "every addon without a
        // plugin.cfg is wrong".
        var fileSystem = TreeWith("shader.gdshader", "textures/ramp.png");

        Assert.Null(GodotProjectRootChecker.Check(fileSystem, Tree, "mtoon-shader"));
    }

    [Fact]
    public void Check_MissingTree_IsLeftAlone() {
        var fileSystem = new InMemoryFileSystem();

        Assert.Null(GodotProjectRootChecker.Check(fileSystem, Tree, "vrm"));
    }

    // -----------------------------------------------------------------------------------------
    // Guessing where the addon went
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Check_SeveralPluginsOneMatchingTheAddonName_SuggestsThatOne() {
        // vrm-addon's real layout: the plugin plus its test framework, both under addons/.
        var fileSystem = TreeWith("project.godot", "addons/gut/plugin.cfg", "addons/vrm/plugin.cfg");

        var finding = GodotProjectRootChecker.Check(fileSystem, Tree, "vrm");

        Assert.NotNull(finding);
        Assert.Equal("addons/vrm", finding.SuggestedPath);
        Assert.Equal(new[] { "addons/gut", "addons/vrm" }, finding.Candidates);
    }

    [Fact]
    public void Check_NameMatchIgnoresCase() {
        // The manifest key is `Gut`, the folder is `gut` — the same tolerance the folder-case
        // correction (SPEC.md §7.1.1) extends, since the repository decides the casing.
        var fileSystem = TreeWith("project.godot", "addons/gut/plugin.cfg", "addons/other/plugin.cfg");

        var finding = GodotProjectRootChecker.Check(fileSystem, Tree, "Gut");

        Assert.Equal("addons/gut", finding?.SuggestedPath);
    }

    [Fact]
    public void Check_SeveralPluginsNoneMatchingTheName_SuggestsNothingButListsThem() {
        var fileSystem = TreeWith("project.godot", "addons/alpha/plugin.cfg", "addons/beta/plugin.cfg");

        var finding = GodotProjectRootChecker.Check(fileSystem, Tree, "vrm");

        // Guessing here would be picking one of two plugins at random and writing it into advice
        // somebody will paste. Listing them is the honest answer.
        Assert.NotNull(finding);
        Assert.Null(finding.SuggestedPath);
        Assert.Equal(new[] { "addons/alpha", "addons/beta" }, finding.Candidates);
        Assert.Contains("'addons/alpha'", GodotProjectRootChecker.DescribeWhereTheAddonIs(finding));
        Assert.Contains("'addons/beta'", GodotProjectRootChecker.DescribeWhereTheAddonIs(finding));
    }

    [Fact]
    public void Check_ExactlyOnePluginUnderAnyName_SuggestsIt() {
        // Renamed on the way in (`install_as`, or a manifest key that never matched the folder):
        // one plugin is still an unambiguous answer.
        var fileSystem = TreeWith("project.godot", "addons/godot_vrm/plugin.cfg");

        var finding = GodotProjectRootChecker.Check(fileSystem, Tree, "vrm");

        Assert.Equal("addons/godot_vrm", finding?.SuggestedPath);
    }

    [Fact]
    public void Check_AddonsFolderWithoutAnyPluginCfg_SuggestsNothing() {
        var fileSystem = TreeWith("project.godot", "addons/notes.txt", "addons/stray/readme.md");

        var finding = GodotProjectRootChecker.Check(fileSystem, Tree, "vrm");

        Assert.NotNull(finding);
        Assert.False(finding.HasCandidates);
        Assert.Null(finding.SuggestedPath);
        Assert.Contains("no addons/*/plugin.cfg", GodotProjectRootChecker.DescribeWhereTheAddonIs(finding));
    }

    // -----------------------------------------------------------------------------------------
    // The suggested path is the value `path` should be set to
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("./")]
    public void Check_ConfiguredPathMeaningTheRoot_LeavesTheSuggestionUnprefixed(string? configuredPath) {
        var fileSystem = TreeWith("project.godot", "addons/vrm/plugin.cfg");

        var finding = GodotProjectRootChecker.Check(fileSystem, Tree, "vrm", configuredPath);

        Assert.Equal("addons/vrm", finding?.SuggestedPath);
    }

    [Theory]
    [InlineData("godot", "godot/addons/vrm")]
    [InlineData("godot/", "godot/addons/vrm")]
    [InlineData("/godot/", "godot/addons/vrm")]
    [InlineData("src\\game", "src/game/addons/vrm")]
    public void Check_ConfiguredSubfolder_PrefixesTheSuggestionSoItCanBePastedAsIs(string configuredPath, string expected) {
        // `path = "godot"` pointing at a project inside a monorepo: the fix is `godot/addons/vrm`,
        // and asking the reader to prepend that themselves is one more place to get it wrong.
        var fileSystem = TreeWith("project.godot", "addons/vrm/plugin.cfg");

        var finding = GodotProjectRootChecker.Check(fileSystem, Tree, "vrm", configuredPath);

        Assert.Equal(expected, finding?.SuggestedPath);
    }

    // -----------------------------------------------------------------------------------------
    // The override hint
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void OverrideCommand_PlainValues_AreLeftBare() {
        Assert.Equal(
            "dotquay override vrm --branch main --path addons/vrm",
            GodotProjectRootChecker.OverrideCommand("vrm", "--branch", "main", "addons/vrm"));
    }

    [Fact]
    public void OverrideCommand_VersionRange_IsQuotedForTheShell() {
        // `~1.4` and `>=1.0` both mean something to a shell before dotquay ever sees them.
        Assert.Equal(
            "dotquay override vrm --version \"~1.4\" --path addons/vrm",
            GodotProjectRootChecker.OverrideCommand("vrm", "--version", "~1.4", "addons/vrm"));
    }

    [Fact]
    public void OverrideCommand_PathWithSpaces_IsQuotedForTheShell() {
        Assert.Equal(
            "dotquay override vrm --rev abc123 --path \"my addons/vrm\"",
            GodotProjectRootChecker.OverrideCommand("vrm", "--rev", "abc123", "my addons/vrm"));
    }
}
