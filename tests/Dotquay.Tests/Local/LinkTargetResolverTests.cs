using Dotquay.Core.Local;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Local;

/// <summary>
/// <see cref="LinkTargetResolver"/>: whether <c>dotquay link &lt;name&gt;</c> means a NuGet package
/// or an addon. Exists because of a real miss — a consumer whose only C# dependency was adopted
/// from an addon (BotV2's <c>VBeami.Core</c>, declared by the vbeami addon's package manifest,
/// present in the lockfile and the <c>.csproj</c>, absent from the consumer's own
/// <c>dotquay.toml</c>) could not link it: the name fell through to an addon link and tried to
/// junction <c>addons/VBeami.Core</c>.
/// </summary>
public class LinkTargetResolverTests {
    private const string Project = "project";

    private static InMemoryFileSystem With(string? manifest, string? lockfile) {
        var fs = new InMemoryFileSystem();
        if (manifest is not null) fs.WriteAllText(fs.CombinePath(Project, "dotquay.toml"),
            manifest);
        if (lockfile is not null) fs.WriteAllText(fs.CombinePath(Project, "dotquay.lock"),
            lockfile);
        return fs;
    }

    private const string DeclaringManifest = """
        [dotnet."Kani.Core"]
        version = "1.0.0"
        """;

    private const string AddonOnlyManifest = """
        [addons.vbeami]
        git = "https://example.test/vbeami.git"
        version = "~0.7"
        """;

    // What `update` writes for a package an addon declared: it lands in the lockfile (and the
    // .csproj), never in the consumer's manifest.
    private const string AdoptedLockfile = """
        [[addon]]
        name = "vbeami"
        git = "https://example.test/vbeami.git"
        resolved = "0816c20c170c23ae921209c40436f5ab8da62ae5"
        version = "v0.7.20"
        path = "godot/addons/vbeami"
        content_hash = "sha256:0000000000000000000000000000000000000000000000000000000000000000"

        [[dotnet]]
        name = "VBeami.Core"
        resolved = "0.7.20"
        source = "vbeami"
        """;

    [Fact]
    public void ADeclaredPackage_IsADotnetLink() {
        var fs = With(DeclaringManifest, null);

        Assert.Equal("Kani.Core",
            LinkTargetResolver.ResolveDotnetPackageId(fs, Project, "Kani.Core"));
    }

    [Fact]
    public void AnAdoptedPackage_IsADotnetLink_EvenThoughTheManifestNeverNamesIt() {
        // The motivating case. The manifest only knows the addon; the lockfile knows what the
        // addon brought in.
        var fs = With(AddonOnlyManifest, AdoptedLockfile);

        Assert.Equal("VBeami.Core",
            LinkTargetResolver.ResolveDotnetPackageId(fs, Project, "VBeami.Core"));
    }

    [Fact]
    public void TheProjectsOwnSpellingIsReturned_FromEitherSource() {
        // Everything downstream keys on the returned string, so a differently-cased request must
        // not create a second link entry.
        Assert.Equal("Kani.Core",
            LinkTargetResolver.ResolveDotnetPackageId(With(DeclaringManifest, null), Project,
                "kani.core"));
        Assert.Equal("VBeami.Core",
            LinkTargetResolver.ResolveDotnetPackageId(With(AddonOnlyManifest, AdoptedLockfile),
                Project, "vbeami.CORE"));
    }

    [Fact]
    public void AnAddonName_IsNotADotnetLink() {
        // 'vbeami' is in the manifest's [addons] and in the lockfile's [[addon]] — neither is a
        // package, so this must stay an addon link.
        var fs = With(AddonOnlyManifest, AdoptedLockfile);

        Assert.Null(LinkTargetResolver.ResolveDotnetPackageId(fs, Project, "vbeami"));
    }

    [Fact]
    public void TheManifestWins_WhenBothNameThePackage() {
        var fs = With("""
            [dotnet."vbeami.core"]
            version = "0.7.20"
            """, AdoptedLockfile);

        // Deliberately mismatched casing between the two files: the manifest's is the one that
        // dotquay.local.toml and the overlay must key on.
        Assert.Equal("vbeami.core",
            LinkTargetResolver.ResolveDotnetPackageId(fs, Project, "VBeami.Core"));
    }

    [Fact]
    public void NoManifestAndNoLockfile_FallsBackToAddon() {
        Assert.Null(LinkTargetResolver.ResolveDotnetPackageId(With(null, null), Project,
            "anything"));
    }

    [Fact]
    public void UnreadableFiles_DoNotBlockLinking() {
        // `link` must not invent a prerequisite: a broken file is a problem for the commands that
        // need it, and the pre-v0.11 answer (addon link) is the safe default.
        var fs = With("this is [not toml", "[[dotnet]]\nname = \n");

        Assert.Null(LinkTargetResolver.ResolveDotnetPackageId(fs, Project, "VBeami.Core"));
    }

    [Fact]
    public void ABrokenManifest_StillLetsTheLockfileAnswer() {
        var fs = With("this is [not toml", AdoptedLockfile);

        Assert.Equal("VBeami.Core",
            LinkTargetResolver.ResolveDotnetPackageId(fs, Project, "VBeami.Core"));
    }
}
