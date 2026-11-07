using Dotquay.Core.Manifest;
using Xunit;

namespace Dotquay.Tests.Manifest;

/// <summary>
/// Parses the exact manifests shipped in the vbeami repository.
/// </summary>
/// <remarks>
/// Not a test of vbeami — a test of the two shapes it is the first real example of: a project
/// declaring a private feed with package-ID mapping, and an addon propagating that feed to its
/// consumers (SPEC.md §11.1, §11.3). Both were written by hand, and a hand-written manifest that
/// silently fails to declare a feed surfaces much later as an unexplained NU1101 during restore,
/// which is exactly the failure this file exists to catch early.
/// </remarks>
public class VbeamiManifestShapeTests {
    private const string FeedUrl =
        "https://gitlab.kemomi.de/api/v4/projects/124/packages/nuget/index.json";

    private const string ProjectManifest = """
        [nuget.sources.vbeami]
        url = "https://gitlab.kemomi.de/api/v4/projects/124/packages/nuget/index.json"
        packages = ["VBeami.*"]

        [addons.mtoon-shader]
        git = "git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git"
        rev = "dd4dd78fb4c682f9d22abe9d336da033466b76de"
        path = "."
        install_as = "Godot-MToon-Shader"

        [addons.vrm]
        git = "git@gitlab.kemomi.de:wild-alliance/vrm-addon.git"
        rev = "2f8157adba56f571686e77782b3133be998c56df"
        path = "."

        [dotnet.VBeami.Core]
        version = "0.0.2"
        source = "vbeami"
        """;

    private const string PackageManifest = """
        [package]
        name = "vbeami"

        [nuget.sources.vbeami]
        url = "https://gitlab.kemomi.de/api/v4/projects/124/packages/nuget/index.json"
        packages = ["VBeami.*"]

        [addons.mtoon-shader]
        git = "git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git"
        rev = "dd4dd78fb4c682f9d22abe9d336da033466b76de"
        path = "."
        install_as = "Godot-MToon-Shader"

        [addons.vrm]
        git = "git@gitlab.kemomi.de:wild-alliance/vrm-addon.git"
        rev = "2f8157adba56f571686e77782b3133be998c56df"
        path = "."

        [dotnet.VBeami.Core]
        version = "0.0.2"
        source = "vbeami"
        """;

    [Fact]
    public void Project_DeclaresTheFeedWithItsPackageMapping() {
        var manifest = ManifestReader.ReadProject(ProjectManifest);

        var source = Assert.Single(manifest.NuGetSources);
        Assert.Equal("vbeami", source.Name);
        Assert.Equal(FeedUrl, source.Url);
        // Without this, restore may look for a VBeami.* package on nuget.org and take whatever it
        // finds under that name (SPEC.md §11.1).
        Assert.Equal(["VBeami.*"], source.Packages);
    }

    [Fact]
    public void Project_PinsTheDotnetPackageToThatFeed() {
        var manifest = ManifestReader.ReadProject(ProjectManifest);

        var package = manifest.Dotnet["VBeami.Core"];
        Assert.Equal("0.0.2", package.Version);
        Assert.Equal("vbeami", package.Source);
    }

    [Fact]
    public void Project_SourcePinIsValidatedAgainstDeclaredSources() {
        // The pin only means anything if a typo is caught. A `source` naming a feed that was never
        // declared would otherwise resolve to nothing and fall back to searching everything.
        var typo = ProjectManifest.Replace("source = \"vbeami\"", "source = \"vbeamii\"");

        var exception = Assert.Throws<ManifestParseException>(() =>
            ManifestReader.ReadProject(typo));
        Assert.Contains("vbeamii", exception.Message);
    }

    [Fact]
    public void Project_KeepsInstallAsAndTheAddonGraph() {
        var manifest = ManifestReader.ReadProject(ProjectManifest);

        Assert.Equal(2, manifest.Addons.Count);
        Assert.Equal("Godot-MToon-Shader", manifest.Addons["mtoon-shader"].EffectiveFolder);
        Assert.Equal("vrm", manifest.Addons["vrm"].EffectiveFolder);
    }

    [Fact]
    public void Package_PropagatesTheFeedAttributedToTheAddon() {
        var manifest = ManifestReader.ReadPackage(PackageManifest);

        var source = Assert.Single(manifest.NuGetSources);
        Assert.Equal(FeedUrl, source.Url);
        // Attribution is what lets a consuming project say *who* asked for a feed, and what doctor
        // answers "why is this here?" with (SPEC.md §11.3).
        Assert.Equal("vbeami", source.DeclaredByAddon);
    }

    [Fact]
    public void Package_StillDeclaresItsAddonsAndPackage() {
        var manifest = ManifestReader.ReadPackage(PackageManifest);

        Assert.Equal("vbeami", manifest.Name);
        Assert.Equal(2, manifest.Addons.Count);
        Assert.Equal("0.0.2", manifest.Dotnet["VBeami.Core"].Version);
    }

    [Fact]
    public void Project_RoundTripsThroughTheWriter() {
        // dotquay rewrites dotquay.toml wholesale on every mutating command, so anything the writer
        // drops is deleted from a hand-written file the next time an unrelated `add` runs.
        var original = ManifestReader.ReadProject(ProjectManifest);

        var reparsed = ManifestReader.ReadProject(ManifestWriter.Serialize(original));

        var source = Assert.Single(reparsed.NuGetSources);
        Assert.Equal(FeedUrl, source.Url);
        Assert.Equal(["VBeami.*"], source.Packages);
        Assert.Equal("vbeami", reparsed.Dotnet["VBeami.Core"].Source);
        Assert.Equal("Godot-MToon-Shader", reparsed.Addons["mtoon-shader"].EffectiveFolder);
    }
}
