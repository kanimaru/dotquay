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

        Assert.Null(manifest.NuGetSource);
        Assert.Empty(manifest.Addons);
    }

    [Fact]
    public void ScaffoldProjectToml_WithNuGetSource_RecordsIt()
    {
        var toml = ManifestWriter.ScaffoldProjectToml(nuGetSource: "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json");
        var manifest = ManifestReader.ReadProject(toml);

        Assert.Equal("https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json", manifest.NuGetSource);
    }

    [Fact]
    public void Serialize_ThenRead_RoundTripsNuGetSource()
    {
        var manifest = new ProjectManifest { NuGetSource = "https://private.example.com/index.json" };

        var toml = ManifestWriter.Serialize(manifest);
        var roundTripped = ManifestReader.ReadProject(toml);

        Assert.Equal("https://private.example.com/index.json", roundTripped.NuGetSource);
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
