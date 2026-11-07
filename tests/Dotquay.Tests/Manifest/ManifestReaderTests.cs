using Dotquay.Core.Manifest;
using Xunit;

namespace Dotquay.Tests.Manifest;

public class ManifestReaderTests {
    [Fact]
    public void ReadProject_ParsesGitVersionBranchAndRevSources() {
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

        Assert.Equal(3, manifest.Addons.Count);

        var dialogueManager = manifest.Addons["dialogue_manager"];
        Assert.Equal(AddonSourceKind.Version, dialogueManager.SourceKind);
        Assert.Equal("~2.44", dialogueManager.Version);
        Assert.Equal("addons/dialogue_manager", dialogueManager.EffectivePath);

        var someAddon = manifest.Addons["some_addon"];
        Assert.Equal(AddonSourceKind.Branch, someAddon.SourceKind);
        Assert.Equal("godot4", someAddon.Branch);
        // default path, no explicit "path" key
        Assert.Equal("addons/some_addon", someAddon.EffectivePath);

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
    public void ReadProject_RejectsAddonsWithoutExactlyOneSource(string toml) {
        var ex = Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject(toml));
        Assert.Contains("exactly one of", ex.Message);
    }

    [Fact]
    public void ReadProject_RejectsMissingGitKey() {
        const string toml = """
            [addons.bad]
            version = "1.0"
            """;

        var ex = Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject(toml));
        Assert.Contains("git", ex.Message);
    }

    [Fact]
    public void ReadProject_RejectsMalformedToml() {
        const string toml = "this is not valid toml [[[";
        Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject(toml));
    }

    [Fact]
    public void ReadProject_ToleratesMissingOptionalSections() {
        var manifest = ManifestReader.ReadProject("");

        Assert.Empty(manifest.NuGetSources);
        Assert.Empty(manifest.Addons);
        Assert.Empty(manifest.Dotnet);
        Assert.Empty(manifest.Overrides);
        Assert.True(manifest.NuGetSourceMapping);
    }

    [Fact]
    public void ReadProject_IgnoresTheRetiredGodotKey() {
        // [project] godot was removed after v0.5 (nothing ever read it), but manifests in the wild
        // still carry it and dotquay.toml is a file people hand-edit — an unknown key must not be a
        // parse error.
        var manifest = ManifestReader.ReadProject("""
            [project]
            godot = "4.7"
            nuget_source = "https://private.example.com/index.json"
            """);

        var source = Assert.Single(manifest.NuGetSources);
        Assert.Equal(NuGetSourceSpec.LegacyName, source.Name);
        Assert.Equal("https://private.example.com/index.json", source.Url);
    }

    [Fact]
    public void ReadProject_NamedSources_KeepDeclarationOrder() {
        // Order is the resolution rule (SPEC.md §11.1), so it is read out of the file rather than
        // out of a dictionary whose enumeration order is unspecified.
        var manifest = ManifestReader.ReadProject("""
            [nuget.sources.tools]
            url = "https://private.example.com/tools/index.json"

            [nuget.sources.core]
            url = "https://private.example.com/core/index.json"
            """);

        Assert.Equal(["tools", "core"], manifest.NuGetSources.Select(source => source.Name));
    }

    [Fact]
    public void ReadProject_LegacyKeyPlusExplicitDefault_IsAnErrorRatherThanASilentPrecedenceRule() {
        var exception = Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject("""
            [project]
            nuget_source = "https://a.example.com/index.json"

            [nuget.sources.default]
            url = "https://b.example.com/index.json"
            """));

        Assert.Contains("Keep one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadProject_InvalidSourceName_IsRejectedAtParseTime() {
        // The name becomes part of an environment variable name and a NuGet.Config key, so it can't
        // be arbitrary.
        Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject("""
            [nuget.sources."has space"]
            url = "https://private.example.com/index.json"
            """));
    }

    [Fact]
    public void ReadProject_SourceMissingUrl_SaysWhichKeyIsMissing() {
        var exception = Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject("""
            [nuget.sources.core]
            """));

        Assert.Contains("'url'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadProject_SourceMapping_DefaultsTrueAndCanBeTurnedOff() {
        Assert.False(ManifestReader.ReadProject("""
            [nuget]
            source_mapping = false
            """).NuGetSourceMapping);
    }

    [Fact]
    public void ReadProject_DotnetSourcePin_IsReadAsAPin() {
        var manifest = ManifestReader.ReadProject("""
            [nuget.sources.core]
            url = "https://private.example.com/core/index.json"

            [dotnet.Acme.Internal]
            version = "1.0.0"
            source = "core"
            """);

        Assert.Equal("core", manifest.Dotnet["Acme.Internal"].Source);
    }

    [Fact]
    public void ReadProject_DotnetSourceAsATable_IsAPackageIdSegmentNotAPin() {
        // Told apart by TOML type: a string 'source' is a feed pin, a *table* named 'source' is
        // part of a dotted package ID. A key can't be both in TOML, so the two never collide.
        var manifest = ManifestReader.ReadProject("""
            [dotnet.Foo.source]
            version = "2.0.0"
            """);

        Assert.Equal("2.0.0", manifest.Dotnet["Foo.source"].Version);
        Assert.Null(manifest.Dotnet["Foo.source"].Source);
    }

    [Fact]
    public void ReadProject_PinNamingAnUndeclaredSource_ListsTheOnesThatExist() {
        var exception = Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject("""
            [nuget.sources.core]
            url = "https://private.example.com/core/index.json"

            [dotnet.Acme.Internal]
            version = "1.0.0"
            source = "typo"
            """));

        Assert.Contains("'core'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadProject_PinNamingNuGetOrg_IsAllowedWithoutDeclaringIt() {
        // nuget.org is appended to every source set implicitly, so pinning to it must not require
        // writing it out first.
        var manifest = ManifestReader.ReadProject("""
            [dotnet.Newtonsoft.Json]
            version = "13.0.3"
            source = "nuget.org"
            """);

        Assert.Equal("nuget.org", manifest.Dotnet["Newtonsoft.Json"].Source);
    }

    [Fact]
    public void ReadPackage_RequiresPackageHeader() {
        const string toml = """
            [addons.core]
            git = "https://example.com/core"
            version = "1.0"
            """;

        var ex = Assert.Throws<ManifestParseException>(() => ManifestReader.ReadPackage(toml));
        Assert.Contains("[package]", ex.Message);
    }

    [Fact]
    public void ReadPackage_ParsesNameAndVersion() {
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

    [Fact]
    public void ReadPackage_VersionIsOptional() {
        // Nothing reads it: the git tag decides resolution and plugin.cfg is what doctor checks for
        // drift. Requiring it made a third place to bump, which is a third place to forget.
        var package = ManifestReader.ReadPackage("""
            [package]
            name = "kani_dialogue_tools"

            [addons.kani_core]
            git = "https://github.com/kani/kani_core"
            version = "~1.4"
            """);

        Assert.Equal("kani_dialogue_tools", package.Name);
        Assert.Null(package.Version);
        Assert.Single(package.Addons);
    }

    [Fact]
    public void ReadPackage_StillRequiresName() {
        var ex = Assert.Throws<ManifestParseException>(() => ManifestReader.ReadPackage("""
            [package]
            version = "1.2.0"
            """));

        Assert.Contains("'name'", ex.Message);
    }
}
