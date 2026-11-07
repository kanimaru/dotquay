using Dotquay.Core.Manifest;
using Dotquay.Core.Workflows;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <see cref="AddonNaming"/> — which <c>[addons.&lt;name&gt;]</c> key <c>dotquay add</c>
/// writes, including a second addon out of a repository that ships several (the BotV2 /
/// <c>vbeami_vcam</c> report).
/// </summary>
public class AddonNamingTests {
    private const string VbeamiUrl = "https://gitlab.kemomi.de/wild-alliance/vbeami/";
    private const string ManifestPath = "project/dotquay.toml";

    private static Dictionary<string, AddonSpec> Declared(params AddonSpec[] specs) =>
        specs.ToDictionary(s => s.Name, StringComparer.Ordinal);

    private static AddonSpec Vbeami(string name = "vbeami", string? path = "godot/addons/vbeami",
        string git = VbeamiUrl) =>
        new() { Name = name, Git = git, Branch = "main", Path = path };

    // -- the historical default is untouched ------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/nathanhoad/godot_dialogue_manager.git",
        "godot_dialogue_manager")]
    [InlineData("https://gitlab.kemomi.de/wild-alliance/vbeami/", "vbeami")]
    [InlineData("git@github.com:kanimaru/twitcher.git", "twitcher")]
    [InlineData("git@gitlab.kemomi.de:wild-alliance/dotquay.git", "dotquay")]
    [InlineData("file:///F:/projects/godot/vbeami", "vbeami")]
    public void DeriveFromUrl_TakesTheRepositoryName(string url, string expected) {
        Assert.Equal(expected, AddonNaming.DeriveFromUrl(url));
    }

    [Fact]
    public void Choose_FreeUrlName_IsUsedEvenWithPathAndInstallAs() {
        // Pinned by an older workflow test too: --path alone never renames an addon whose URL name
        // is free. The fallback only exists for the case that used to be refused outright.
        var choice = AddonNaming.Choose(
            VbeamiUrl,
            new AddonAddOptions { Path = "godot/addons/vbeami_vcam", InstallAs = "vbeami_vcam" },
            Declared(),
            ManifestPath);

        Assert.Equal(new AddonNameChoice("vbeami", AddonNameSource.Url), choice);
        Assert.False(choice.IsFallback);
    }

    // -- the reported bug: a second subtree of the same repo --------------------------------------

    [Fact]
    public void Choose_SecondSubtreeOfSameRepo_FallsBackToInstallAs() {
        var choice = AddonNaming.Choose(
            VbeamiUrl,
            new AddonAddOptions {
                Branch = "main",
                Path = "godot/addons/vbeami_vcam",
                InstallAs = "vbeami_vcam"
            },
            Declared(Vbeami()),
            ManifestPath);

        Assert.Equal("vbeami_vcam", choice.Name);
        Assert.Equal(AddonNameSource.InstallAs, choice.Source);
        Assert.Equal("vbeami", choice.DisplacedBy);
        Assert.True(choice.IsFallback);
    }

    [Theory]
    [InlineData("godot/addons/vbeami_vcam")]
    [InlineData("godot/addons/vbeami_vcam/")]
    [InlineData("godot\\addons\\vbeami_vcam")]
    public void Choose_SecondSubtreeWithoutInstallAs_FallsBackToPathLeaf(string path) {
        var choice = AddonNaming.Choose(VbeamiUrl, new AddonAddOptions { Path = path },
            Declared(Vbeami()), ManifestPath);

        Assert.Equal("vbeami_vcam", choice.Name);
        Assert.Equal(AddonNameSource.PathLeaf, choice.Source);
    }

    [Fact]
    public void Choose_InstallAsTaken_FallsThroughToPathLeaf() {
        var choice = AddonNaming.Choose(
            VbeamiUrl,
            new AddonAddOptions { Path = "godot/addons/vbeami_vcam", InstallAs = "cam" },
            Declared(Vbeami(), Vbeami(name: "cam", path: "godot/addons/other_cam")),
            ManifestPath);

        Assert.Equal(new AddonNameChoice("vbeami_vcam", AddonNameSource.PathLeaf, "vbeami"),
            choice);
    }

    [Fact]
    public void Choose_SameRepoOverAnotherTransport_IsStillTheSameRepository() {
        // GitUrl identity (v0.15): the ssh spelling of the repo is the same repo, so asking for the
        // same subtree again over ssh is a re-add, not a second addon.
        var exception = Assert.Throws<WorkflowException>(() => AddonNaming.Choose(
            "git@gitlab.kemomi.de:wild-alliance/vbeami.git",
            new AddonAddOptions { Path = "godot/addons/vbeami/" },
            Declared(Vbeami()),
            ManifestPath));

        Assert.Contains("dotquay update vbeami", exception.Message, StringComparison.Ordinal);
    }

    // -- the accidental re-add keeps its old refusal ---------------------------------------------

    [Fact]
    public void Choose_SameRepoNoPathNamed_RefusesWithUpdateHint() {
        // No subtree named means "the one I already have", even though the default guess
        // (addons/vbeami) differs from the declared godot/addons/vbeami.
        var exception = Assert.Throws<WorkflowException>(
            () =>
                AddonNaming.Choose(VbeamiUrl, new AddonAddOptions(), Declared(Vbeami()),
                    ManifestPath));

        Assert.Contains("already declared", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dotquay update vbeami", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_SameRepoSameSubtree_RefusesEvenWithInstallAs() {
        // Same files from the same repo: a fallback name here would install a second copy of it.
        var exception = Assert.Throws<WorkflowException>(() => AddonNaming.Choose(
            VbeamiUrl,
            new AddonAddOptions { Path = "godot/addons/vbeami", InstallAs = "vbeami_copy" },
            Declared(Vbeami()),
            ManifestPath));

        Assert.Contains("dotquay update vbeami", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_DefaultSubtreeDeclaredWithoutPath_MatchesExplicitDefault() {
        // An entry without `path` extracts addons/<name>; naming that same subtree is the same
        // addon.
        var declared = Declared(Vbeami(path: null));

        Assert.Throws<WorkflowException>(() => AddonNaming.Choose(
            VbeamiUrl, new AddonAddOptions { Path = "addons/vbeami" }, declared, ManifestPath));
    }

    // -- nothing left to fall back to ------------------------------------------------------------

    [Fact]
    public void Choose_DifferentSubtreeNothingToFallBackTo_AsksForName() {
        // The repo root has no leaf, and no --install-as was given.
        var exception = Assert.Throws<WorkflowException>(() => AddonNaming.Choose(
            VbeamiUrl, new AddonAddOptions { Path = "." }, Declared(Vbeami()), ManifestPath));

        Assert.Contains("'vbeami' is already declared", exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("--name <name>", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--install-as <folder>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_EveryCandidateTaken_NamesThemAllAndAsksForName() {
        var exception = Assert.Throws<WorkflowException>(() => AddonNaming.Choose(
            VbeamiUrl,
            new AddonAddOptions { Path = "godot/addons/vbeami_vcam", InstallAs = "vbeami_vcam" },
            Declared(Vbeami(), Vbeami(name: "vbeami_vcam", path: "elsewhere/vbeami_vcam")),
            ManifestPath));

        Assert.Contains("'vbeami' and 'vbeami_vcam' are already declared", exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("--name <name>", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--install-as <folder>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_DifferentRepoSharingTheName_FallsBackToo() {
        // Two forks both called "gut": the second is a different addon, not a re-add.
        var declared = Declared(new AddonSpec {
            Name = "gut",
            Git = "https://github.com/bitwes/Gut.git",
            Version = "~9"
        });

        var choice = AddonNaming.Choose(
            "https://example.com/someone/gut.git",
            new AddonAddOptions { InstallAs = "gut_fork" },
            declared,
            ManifestPath);

        Assert.Equal("gut_fork", choice.Name);
    }

    // -- --name -----------------------------------------------------------------------------------

    [Fact]
    public void Choose_ExplicitName_WinsOverEverything() {
        var choice = AddonNaming.Choose(
            VbeamiUrl,
            new AddonAddOptions {
                Name = "camera",
                Path = "godot/addons/vbeami_vcam",
                InstallAs = "vbeami_vcam"
            },
            Declared(),
            ManifestPath);

        Assert.Equal(new AddonNameChoice("camera", AddonNameSource.Explicit), choice);
    }

    [Fact]
    public void Choose_ExplicitNameTaken_Refuses() {
        var exception = Assert.Throws<WorkflowException>(() => AddonNaming.Choose(
            VbeamiUrl, new AddonAddOptions { Name = "vbeami", Path = "godot/addons/vbeami_vcam" },
            Declared(Vbeami()), ManifestPath));

        Assert.Contains("Pick a different --name", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("C:")]
    [InlineData("vbeami.vcam")]
    public void Choose_ExplicitNameNotAPlainFolderName_Refuses(string name) {
        var exception = Assert.Throws<WorkflowException>(() => AddonNaming.Choose(
            VbeamiUrl, new AddonAddOptions { Name = name }, Declared(), ManifestPath));

        Assert.Contains("not a usable addon name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_UnsafeInstallAs_IsNeverUsedAsAName() {
        // install_as is validated later by the manifest reader; the naming fallback must not turn
        // an unsafe folder into a manifest key on the way there.
        var choice = AddonNaming.Choose(
            VbeamiUrl,
            new AddonAddOptions { Path = "godot/addons/vbeami_vcam", InstallAs = "../escape" },
            Declared(Vbeami()),
            ManifestPath);

        Assert.Equal("vbeami_vcam", choice.Name);
    }

    // -- PathLeaf ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData(".", null)]
    [InlineData("./", null)]
    [InlineData("", null)]
    [InlineData("addons/gut", "gut")]
    [InlineData("/godot/addons/vbeami_vcam/", "vbeami_vcam")]
    [InlineData("src\\Dotquay.Godot\\addons\\dotquay", "dotquay")]
    public void PathLeaf_TakesTheLastSegment(string? path, string? expected) {
        Assert.Equal(expected, AddonNaming.PathLeaf(path));
    }
}
