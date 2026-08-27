using Dotquay.Core.Manifest;
using Xunit;

namespace Dotquay.Tests.Manifest;

public class ManifestReaderTests
{
    [Fact]
    public void ReadProject_ParsesGitVersionBranchAndRevSources()
    {
        const string toml = """
            [project]
            godot = "4.3"

            [addons.dialogue_manager]
            git = "https://github.com/nathanhoad/godot_dialogue_manager"
            version = "~2.44"
            path = "addons/dialogue_manager"

            [addons.some_addon]
            git = "https://github.com/foo/some_addon"
            branch = "godot4"

            [addons.other_addon]
            git = "https://github.com/bar/other_addon"
            rev = "9f2c1ab"

            [dotnet.Newtonsoft.Json]
            version = "13.0.*"

            [overrides.shared_core]
            git = "https://github.com/kani/kani_core"
            rev = "abc123"
            """;

        var manifest = ManifestReader.ReadProject(toml);

        Assert.Equal("4.3", manifest.Godot);
        Assert.Equal(3, manifest.Addons.Count);

        var dialogueManager = manifest.Addons["dialogue_manager"];
        Assert.Equal(AddonSourceKind.Version, dialogueManager.SourceKind);
        Assert.Equal("~2.44", dialogueManager.Version);
        Assert.Equal("addons/dialogue_manager", dialogueManager.EffectivePath);

        var someAddon = manifest.Addons["some_addon"];
        Assert.Equal(AddonSourceKind.Branch, someAddon.SourceKind);
        Assert.Equal("godot4", someAddon.Branch);
        Assert.Equal("addons/some_addon", someAddon.EffectivePath); // default path, no explicit "path" key

        var otherAddon = manifest.Addons["other_addon"];
        Assert.Equal(AddonSourceKind.Rev, otherAddon.SourceKind);
        Assert.Equal("9f2c1ab", otherAddon.Rev);

        // [dotnet.Newtonsoft.Json] is TOML dotted-table syntax, three levels deep — this asserts
        // ManifestReader reassembles it into a single "Newtonsoft.Json" package spec (see the
        // recursive-walk comment on ManifestReader.CollectDotnetSpecs).
        Assert.Single(manifest.Dotnet);
        Assert.Equal("13.0.*", manifest.Dotnet["Newtonsoft.Json"].Version);

        Assert.Single(manifest.Overrides);
        Assert.Equal("abc123", manifest.Overrides["shared_core"].Rev);
    }

    [Theory]
    [InlineData("""
        [addons.bad]
        git = "https://example.com/bad"
        """)]
    [InlineData("""
        [addons.bad]
        git = "https://example.com/bad"
        version = "1.0"
        branch = "main"
        """)]
    public void ReadProject_RejectsAddonsWithoutExactlyOneSource(string toml)
    {
        var ex = Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject(toml));
        Assert.Contains("exactly one of", ex.Message);
    }

    [Fact]
    public void ReadProject_RejectsMissingGitKey()
    {
        const string toml = """
            [addons.bad]
            version = "1.0"
            """;

        var ex = Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject(toml));
        Assert.Contains("git", ex.Message);
    }

    [Fact]
    public void ReadProject_RejectsMalformedToml()
    {
        const string toml = "this is not valid toml [[[";
        Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject(toml));
    }

    [Fact]
    public void ReadProject_ToleratesMissingOptionalSections()
    {
        var manifest = ManifestReader.ReadProject("");

        Assert.Null(manifest.Godot);
        Assert.Empty(manifest.Addons);
        Assert.Empty(manifest.Dotnet);
        Assert.Empty(manifest.Overrides);
    }

    [Fact]
    public void ReadPackage_RequiresPackageHeader()
    {
        const string toml = """
            [addons.core]
            git = "https://example.com/core"
            version = "1.0"
            """;

        var ex = Assert.Throws<ManifestParseException>(() => ManifestReader.ReadPackage(toml));
        Assert.Contains("[package]", ex.Message);
    }

    [Fact]
    public void ReadPackage_ParsesNameAndVersion()
    {
        const string toml = """
            [package]
            name = "kani_dialogue_tools"
            version = "1.2.0"

            [addons.kani_core]
            git = "https://github.com/kani/kani_core"
            version = "~1.4"
            """;

        var package = ManifestReader.ReadPackage(toml);

        Assert.Equal("kani_dialogue_tools", package.Name);
        Assert.Equal("1.2.0", package.Version);
        Assert.Single(package.Addons);
    }
}
