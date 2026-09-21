using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

/// <summary>
/// Covers the subtree guess (docs/DESIGN-v0.20-SUBTREE-GUESS.md §3): where an addon actually lives
/// inside its own repository, when <c>addons/&lt;name&gt;</c> turns out not to be there.
/// </summary>
/// <remarks>
/// The worked example is SproutyDialogs, because it is where this was found: the repository is
/// <c>SproutyLabs/SproutyDialogs</c> and the addon is <c>addons/sprouty_dialogs</c>, which differs by
/// more than case — so <see cref="SubtreeExtractor.ResolveSubtreePath"/>'s capitalisation fix
/// correctly declines and, before this, the user got an error naming only the path that didn't work.
/// <para>
/// Half these tests exist to pin down what is deliberately <em>not</em> matched. The rules are narrow
/// on purpose, and a rule that quietly widens is how dotquay would start inferring layout (SPEC.md
/// §7.1, OQ-2) without anyone deciding to.
/// </para>
/// </remarks>
public class SubtreeCandidatesTests
{
    private const string Repo = "cache/repo";

    private static InMemoryFileSystem RepoWith(params string[] addonFolders)
    {
        var fileSystem = new InMemoryFileSystem();

        foreach (var folder in addonFolders)
        {
            fileSystem.WriteAllText($"{Repo}/{folder}/plugin.cfg", "cfg");
        }

        return fileSystem;
    }

    // -- nothing to guess -------------------------------------------------------------------------

    [Fact]
    public void Find_GuessIsCorrect_ReturnsNothing()
    {
        var fileSystem = RepoWith("addons/dialogue_manager");

        Assert.Null(SubtreeCandidates.Find(fileSystem, Repo, "addons/dialogue_manager"));
    }

    [Fact]
    public void Find_GuessOnlyDiffersByCase_ReturnsNothing()
    {
        // Already handled, silently and correctly, by the casing fix. Re-reporting it here would ask
        // the user to confirm a correction dotquay is entitled to make on its own.
        var fileSystem = RepoWith("addons/gut");

        Assert.Null(SubtreeCandidates.Find(fileSystem, Repo, "addons/Gut"));
    }

    [Fact]
    public void Find_RepositoryNotFetched_ReturnsNothing() =>
        Assert.Null(SubtreeCandidates.Find(new InMemoryFileSystem(), Repo, "addons/anything"));

    // -- rule 1: the same words in a different convention -----------------------------------------

    [Theory]
    [InlineData("SproutyDialogs", "sprouty_dialogs")]
    [InlineData("SproutyDialogs", "sprouty-dialogs")]
    [InlineData("phantom_camera", "PhantomCamera")]
    [InlineData("gd_ai_3", "GdAi3")]
    public void Find_SameWordsDifferentConvention_IsSuggested(string guessed, string actual)
    {
        var fileSystem = RepoWith($"addons/{actual}");

        var finding = SubtreeCandidates.Find(fileSystem, Repo, $"addons/{guessed}");

        Assert.NotNull(finding);
        Assert.Equal($"addons/{actual}", finding!.Suggestion?.Path);
        Assert.Equal(actual, finding.Suggestion?.Folder);
        Assert.Equal(SubtreeMatchKind.NameShape, finding.Suggestion?.Kind);
        Assert.Equal("name_shape", finding.Suggestion?.Reason);
    }

    [Fact]
    public void Find_NameShapeMatch_WinsOverOtherPlugins()
    {
        var fileSystem = RepoWith("addons/sprouty_dialogs", "addons/sprouty_debug");

        var finding = SubtreeCandidates.Find(fileSystem, Repo, "addons/SproutyDialogs");

        Assert.Equal("addons/sprouty_dialogs", finding?.Suggestion?.Path);
    }

    [Fact]
    public void Find_NameShapeMatch_DoesNotNeedAPluginCfg()
    {
        // An asset-only addon — shaders, fonts, a sprite pack — ships no plugin.cfg and is still an
        // addon. The name is the evidence here, so the structural marker isn't required.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Repo}/addons/pixel_fonts/font.ttf", "not a plugin");

        Assert.Equal("addons/pixel_fonts", SubtreeCandidates.Find(fileSystem, Repo, "addons/PixelFonts")?.Suggestion?.Path);
    }

    [Fact]
    public void Find_DroppedWord_IsNotAMatch()
    {
        // godot_dialogue_manager -> addons/dialogue_manager is real and common, and deliberately not
        // matched by name: the same rule would match 'core' against 'vbeami_core', which is a
        // different addon rather than the same one spelled differently. Rule 2 catches this
        // repository anyway, which is the point of having a second rule instead of a looser first one.
        var fileSystem = RepoWith("addons/dialogue_manager", "addons/dialogue_manager_examples");

        var finding = SubtreeCandidates.Find(fileSystem, Repo, "addons/godot_dialogue_manager");

        Assert.NotNull(finding);
        Assert.Null(finding!.Suggestion);
        Assert.Equal(2, finding.Candidates.Count);
    }

    // -- rule 2: the only addon in the repository -------------------------------------------------

    [Fact]
    public void Find_SingleUnrelatedlyNamedAddon_IsSuggested()
    {
        var fileSystem = RepoWith("addons/dialogue_manager");

        var finding = SubtreeCandidates.Find(fileSystem, Repo, "addons/godot_dialogue_manager");

        Assert.Equal("addons/dialogue_manager", finding?.Suggestion?.Path);
        Assert.Equal(SubtreeMatchKind.OnlyAddon, finding?.Suggestion?.Kind);
        Assert.Equal("only_addon", finding?.Suggestion?.Reason);
    }

    [Fact]
    public void Find_SeveralAddons_SuggestsNothingAndListsThem()
    {
        var fileSystem = RepoWith("addons/one", "addons/two", "addons/three");

        var finding = SubtreeCandidates.Find(fileSystem, Repo, "addons/something");

        Assert.NotNull(finding);
        Assert.Null(finding!.Suggestion);
        Assert.Equal(
            new[] { "addons/one", "addons/three", "addons/two" },
            finding.Candidates.Select(c => c.Path).ToArray());
        Assert.Contains("none is obviously this one", finding.DescribeWhereTheAddonIs());
    }

    [Fact]
    public void Find_FolderWithoutAPluginCfg_IsNotOfferedOnNameAlone()
    {
        // 'examples' is not this addon under another name and has no plugin.cfg, so there is nothing
        // to say about it. Suggesting it would be a guess with no evidence behind it at all.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Repo}/addons/examples/demo.tscn", "scene");

        var finding = SubtreeCandidates.Find(fileSystem, Repo, "addons/whatever");

        Assert.NotNull(finding);
        Assert.Null(finding!.Suggestion);
        Assert.Empty(finding.Candidates);
        Assert.Contains("nothing in its repository looks like an addon folder", finding.DescribeWhereTheAddonIs());
    }

    // -- the two other shapes a repository comes in -----------------------------------------------

    [Fact]
    public void Find_AddonAtTheRepositoryRoot_SuggestsDot()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Repo}/plugin.cfg", "cfg");
        fileSystem.WriteAllText($"{Repo}/thing.gd", "code");

        var finding = SubtreeCandidates.Find(fileSystem, Repo, "addons/thing");

        Assert.Equal(".", finding?.Suggestion?.Path);

        // The folder stays what was being looked for: the cache directory this was read from is named
        // after a URL and a commit, and a root-level addon says nothing about what it wants to be
        // called.
        Assert.Equal("thing", finding?.Suggestion?.Folder);
        Assert.Contains("the addon is the repository itself", finding!.DescribeWhereTheAddonIs());
    }

    [Fact]
    public void Find_GodotProjectInASubfolder_LooksOneLevelDown()
    {
        var fileSystem = RepoWith("godot/addons/vbeami");

        Assert.Equal(
            "godot/addons/vbeami",
            SubtreeCandidates.Find(fileSystem, Repo, "addons/vbeami")?.Suggestion?.Path);
    }

    [Fact]
    public void Find_RootAddonsFolderWins_OverANestedOne()
    {
        // Both exist. Considering both would turn one clear answer into an ambiguous list, and the
        // root is the layout the guess was written against.
        var fileSystem = RepoWith("addons/sprouty_dialogs", "demo/addons/sprouty_dialogs");

        var finding = SubtreeCandidates.Find(fileSystem, Repo, "addons/SproutyDialogs");

        Assert.Equal("addons/sprouty_dialogs", finding?.Suggestion?.Path);
        Assert.Single(finding!.Candidates);
    }

    // -- normalisation ----------------------------------------------------------------------------

    [Theory]
    [InlineData("SproutyDialogs", "sproutydialogs")]
    [InlineData("sprouty_dialogs", "sproutydialogs")]
    [InlineData("Sprouty-Dialogs", "sproutydialogs")]
    [InlineData("R3.Godot", "r3godot")]
    [InlineData("", "")]
    public void Normalize_DropsSeparatorsAndCase(string input, string expected) =>
        Assert.Equal(expected, SubtreeCandidates.Normalize(input));
}
