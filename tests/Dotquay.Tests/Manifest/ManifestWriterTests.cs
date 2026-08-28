using Dotquay.Core.Manifest;
using Xunit;

namespace Dotquay.Tests.Manifest;

public class ManifestWriterTests
{
    [Fact]
    public void Serialize_ThenRead_RoundTrips()
    {
        var manifest = new ProjectManifest
        {
            Addons = new Dictionary<string, AddonSpec>
            {
                ["dialogue_manager"] = new AddonSpec
                {
                    Name = "dialogue_manager",
                    Git = "https://github.com/nathanhoad/godot_dialogue_manager",
                    Version = "~2.44",
                    Path = "addons/dialogue_manager",
                },
                ["some_addon"] = new AddonSpec
                {
                    Name = "some_addon",
                    Git = "https://github.com/foo/some_addon",
                    Branch = "godot4",
                },
            },
            Dotnet = new Dictionary<string, DotnetSpec>
            {
                ["Newtonsoft.Json"] = new DotnetSpec { Name = "Newtonsoft.Json", Version = "13.0.*" },
            },
            Overrides = new Dictionary<string, AddonSpec>
            {
                ["shared_core"] = new AddonSpec
                {
                    Name = "shared_core",
                    Git = "https://github.com/kani/kani_core",
                    Rev = "abc123",
                },
            },
        };

        var toml = ManifestWriter.Serialize(manifest);
        var roundTripped = ManifestReader.ReadProject(toml);

        Assert.Equal(manifest.Addons.Count, roundTripped.Addons.Count);
        Assert.Equal("~2.44", roundTripped.Addons["dialogue_manager"].Version);
        Assert.Equal("godot4", roundTripped.Addons["some_addon"].Branch);
        Assert.Equal("13.0.*", roundTripped.Dotnet["Newtonsoft.Json"].Version);
        Assert.Equal("abc123", roundTripped.Overrides["shared_core"].Rev);
    }

    [Fact]
    public void ScaffoldProjectToml_ProducesValidToml()
    {
        var toml = ManifestWriter.ScaffoldProjectToml();
        var manifest = ManifestReader.ReadProject(toml);

        Assert.Empty(manifest.NuGetSources);
        Assert.Empty(manifest.Addons);
    }

    [Fact]
    public void ScaffoldProjectToml_WithNuGetSource_WritesTheNamedSourceForm()
    {
        var toml = ManifestWriter.ScaffoldProjectToml(nuGetSource: "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json");
        var manifest = ManifestReader.ReadProject(toml);

        var source = Assert.Single(manifest.NuGetSources);
        Assert.Equal(NuGetSourceSpec.LegacyName, source.Name);
        Assert.Equal("https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json", source.Url);

        // Reading the legacy key stays supported forever; writing it stops here, so nothing created
        // from v0.7 onwards has to be migrated twice.
        Assert.DoesNotContain("nuget_source", toml);
    }

    [Fact]
    public void Serialize_ThenRead_RoundTripsEveryDeclaredSourceInOrder()
    {
        // The guard against ProjectFileStore.WriteManifest's wholesale rewrite silently deleting a
        // hand-written multi-feed configuration on the next unrelated `add`.
        var manifest = new ProjectManifest
        {
            NuGetSources =
            [
                new NuGetSourceSpec("core", "https://private.example.com/core/index.json"),
                new NuGetSourceSpec("tools", "https://private.example.com/tools/index.json"),
            ],
        };

        var roundTripped = ManifestReader.ReadProject(ManifestWriter.Serialize(manifest));

        Assert.Equal(["core", "tools"], roundTripped.NuGetSources.Select(source => source.Name));
        Assert.Equal(
            ["https://private.example.com/core/index.json", "https://private.example.com/tools/index.json"],
            roundTripped.NuGetSources.Select(source => source.Url));
    }

    [Fact]
    public void Serialize_ThenRead_RoundTripsAPerPackageSourcePin()
    {
        var manifest = new ProjectManifest
        {
            NuGetSources = [new NuGetSourceSpec("core", "https://private.example.com/core/index.json")],
            Dotnet = new Dictionary<string, DotnetSpec>
            {
                ["Acme.Internal"] = new DotnetSpec { Name = "Acme.Internal", Version = "1.0.0", Source = "core" },
            },
        };

        var roundTripped = ManifestReader.ReadProject(ManifestWriter.Serialize(manifest));

        Assert.Equal("core", roundTripped.Dotnet["Acme.Internal"].Source);
    }

    [Fact]
    public void Serialize_SourceMappingDisabled_EmitsBareNuGetKeysBeforeAnySubTable()
    {
        // Ordering is load-bearing: writing [nuget.sources.core] first implicitly defines [nuget],
        // and a later explicit [nuget] header is a redefinition TOML rejects. Asserted by round-trip,
        // which is exactly the operation that would fail.
        var manifest = new ProjectManifest
        {
            NuGetSourceMapping = false,
            NuGetSources = [new NuGetSourceSpec("core", "https://private.example.com/core/index.json")],
        };

        var toml = ManifestWriter.Serialize(manifest);
        var roundTripped = ManifestReader.ReadProject(toml);

        Assert.False(roundTripped.NuGetSourceMapping);
        Assert.Single(roundTripped.NuGetSources);
        Assert.True(toml.IndexOf("[nuget]", StringComparison.Ordinal) < toml.IndexOf("[nuget.sources", StringComparison.Ordinal));
    }

    [Fact]
    public void ScaffoldPackageToml_ProducesValidToml()
    {
        var toml = ManifestWriter.ScaffoldPackageToml("kani_dialogue_tools");
        var package = ManifestReader.ReadPackage(toml);

        Assert.Equal("kani_dialogue_tools", package.Name);
        Assert.Null(package.Version);
    }

    [Fact]
    public void Serialize_QuotesAddonKeysThatArentBareToml()
    {
        var manifest = new ProjectManifest
        {
            Addons = new Dictionary<string, AddonSpec>
            {
                ["weird name"] = new AddonSpec
                {
                    Name = "weird name",
                    Git = "https://example.com/weird",
                    Rev = "deadbeef",
                },
            },
        };

        var toml = ManifestWriter.Serialize(manifest);
        var roundTripped = ManifestReader.ReadProject(toml);

        Assert.Equal("deadbeef", roundTripped.Addons["weird name"].Rev);
    }
}
