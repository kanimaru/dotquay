using Dotquay.Core.Lockfile;
using Xunit;

namespace Dotquay.Tests.Lockfile;

public class LockfileWriterTests {
    private const string FullSha = "abcdefabcdefabcdefabcdefabcdefabcdefabcd"; // 40 hex chars

    [Fact]
    public void Serialize_ThenRead_RoundTrips() {
        var lockfile = new DotquayLockfile {
            Addons =
            [
                new LockedAddon
                {
                    Name = "dialogue_manager",
                    Git = "https://github.com/nathanhoad/godot_dialogue_manager",
                    Resolved = FullSha,
                    Version = "2.46.1",
                    Path = "addons/dialogue_manager",
                    ContentHash = "sha256:deadbeef",
                    PatchHash = "sha256:cafef00d",
                    PatchBaseSha = FullSha,
                    Dependencies = ["kani_core"],
                },
            ],
            Dotnet =
            [
                new LockedDotnet { Name = "Newtonsoft.Json", Resolved = "13.0.3" },
            ],
        };

        var toml = LockfileWriter.Serialize(lockfile);
        var roundTripped = LockfileReader.Read(toml);

        Assert.Single(roundTripped.Addons);
        Assert.Equal(lockfile.Addons[0].Resolved, roundTripped.Addons[0].Resolved);
        Assert.Equal(lockfile.Addons[0].Dependencies, roundTripped.Addons[0].Dependencies);
        Assert.Equal("sha256:cafef00d", roundTripped.Addons[0].PatchHash);
        Assert.Equal(FullSha, roundTripped.Addons[0].PatchBaseSha);
        Assert.Single(roundTripped.Dotnet);
        Assert.Equal("13.0.3", roundTripped.Dotnet[0].Resolved);
    }

    [Fact]
    public void Serialize_NoPatch_OmitsPatchKeysEntirely() {
        var lockfile = new DotquayLockfile {
            Addons = [new LockedAddon { Name = "clean", Git = "https://example.com/c",
                Resolved = FullSha, Path = "addons/clean" }],
        };

        var toml = LockfileWriter.Serialize(lockfile);

        Assert.DoesNotContain("patch_hash", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("patch_base_sha", toml, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_SortsEntriesByNameForDeterministicOutput() {
        var lockfile = new DotquayLockfile {
            Addons =
            [
                new LockedAddon { Name = "zeta", Git = "https://example.com/z", Resolved = FullSha,
                    Path = "addons/zeta" },
                new LockedAddon { Name = "alpha", Git = "https://example.com/a", Resolved = FullSha,
                    Path = "addons/alpha" },
            ],
        };

        var toml = LockfileWriter.Serialize(lockfile);

        var alphaIndex = toml.IndexOf("\"alpha\"", StringComparison.Ordinal);
        var zetaIndex = toml.IndexOf("\"zeta\"", StringComparison.Ordinal);

        Assert.True(alphaIndex >= 0 && zetaIndex >= 0 && alphaIndex < zetaIndex);
    }
}
