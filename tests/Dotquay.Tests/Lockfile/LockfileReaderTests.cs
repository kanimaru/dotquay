using Dotquay.Core.Lockfile;
using Xunit;

namespace Dotquay.Tests.Lockfile;

public class LockfileReaderTests
{
    private const string FullSha = "abcdefabcdefabcdefabcdefabcdefabcdefabcd"; // 40 hex chars

    [Fact]
    public void Read_ParsesAddonAndDotnetEntries()
    {
        var toml = $"""
            [[addon]]
            name = "dialogue_manager"
            git = "https://github.com/nathanhoad/godot_dialogue_manager"
            resolved = "{FullSha}"
            version = "2.46.1"
            path = "addons/dialogue_manager"
            content_hash = "sha256:deadbeef"
            patch_hash = "sha256:cafef00d"
            patch_base_sha = "{FullSha}"
            dependencies = ["kani_core"]

            [[dotnet]]
            name = "Newtonsoft.Json"
            resolved = "13.0.3"
            """;

        var lockfile = LockfileReader.Read(toml);

        Assert.Single(lockfile.Addons);
        var addon = lockfile.Addons[0];
        Assert.Equal("dialogue_manager", addon.Name);
        Assert.Equal(FullSha, addon.Resolved);
        Assert.Equal("2.46.1", addon.Version);
        Assert.Equal("sha256:deadbeef", addon.ContentHash);
        Assert.Equal("sha256:cafef00d", addon.PatchHash);
        Assert.Equal(FullSha, addon.PatchBaseSha);
        Assert.Equal(["kani_core"], addon.Dependencies);

        Assert.Single(lockfile.Dotnet);
        Assert.Equal("Newtonsoft.Json", lockfile.Dotnet[0].Name);
        Assert.Equal("13.0.3", lockfile.Dotnet[0].Resolved);
    }

    [Fact]
    public void Read_RejectsResolvedThatIsNotAFullCommitSha()
    {
        const string toml = """
            [[addon]]
            name = "bad"
            git = "https://example.com/bad"
            resolved = "9f2c1ab"
            path = "addons/bad"
            """;

        var ex = Assert.Throws<LockfileParseException>(() => LockfileReader.Read(toml));
        Assert.Contains("full", ex.Message);
    }

    [Fact]
    public void Read_RejectsMissingRequiredKey()
    {
        var toml = $"""
            [[addon]]
            name = "bad"
            resolved = "{FullSha}"
            path = "addons/bad"
            """;

        var ex = Assert.Throws<LockfileParseException>(() => LockfileReader.Read(toml));
        Assert.Contains("git", ex.Message);
    }

    [Fact]
    public void Read_ToleratesMissingSections()
    {
        var lockfile = LockfileReader.Read("");

        Assert.Empty(lockfile.Addons);
        Assert.Empty(lockfile.Dotnet);
    }
}
