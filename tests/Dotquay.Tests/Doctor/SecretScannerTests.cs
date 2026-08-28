using Dotquay.Core.Doctor;
using Xunit;

namespace Dotquay.Tests.Doctor;

/// <summary>
/// Covers the manifest secret check. The interesting half is the negatives: <c>dotquay.toml</c> and
/// <c>dotquay.lock</c> are full of commit SHAs and content hashes, and a check that flagged those
/// would be turned off within a day.
/// </summary>
public class SecretScannerTests
{
    [Theory]
    [InlineData("glpat-abcdefghijklmnop")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijkl")]
    [InlineData("gldt-abcdefghijklmnop")]
    public void Describe_ProviderTokens_AreFlagged(string token)
    {
        Assert.NotNull(SecretScanner.Describe($"nuget_source = \"https://x\"\ntoken = \"{token}\""));
    }

    [Fact]
    public void Describe_UrlWithEmbeddedCredentials_IsFlagged()
    {
        var found = SecretScanner.Describe("git = \"https://kani:glsecret@gitlab.example.com/kani/addon.git\"");

        Assert.Contains("embedded", found, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_NeverQuotesWhatItFound()
    {
        const string token = "glpat-abcdefghijklmnop";

        var found = SecretScanner.Describe($"token = \"{token}\"")!;

        // A message naming the secret would put it in terminal scrollback and every bug report that
        // gets pasted from it.
        Assert.DoesNotContain(token, found, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ARealisticManifest_IsClean()
    {
        Assert.Null(SecretScanner.Describe("""
            [nuget.sources.core]
            url = "https://gitlab.example.com/api/v4/projects/123/packages/nuget/index.json"

            [addons.dialogue_manager]
            git = "https://github.com/nathanhoad/godot_dialogue_manager"
            version = "~2.44"
            """));
    }

    [Fact]
    public void Describe_ARealisticLockfile_IsClean()
    {
        // Full of 40-character hex strings and sha256 hashes. Any entropy-based heuristic would light
        // this up, and a check that cries wolf gets ignored.
        Assert.Null(SecretScanner.Describe("""
            [[addon]]
            name = "dialogue_manager"
            resolved = "e4f7a21c9b3d5e6f708192a3b4c5d6e7f8091a2b"
            content_hash = "sha256:9f2c1abf3e4d5c6b7a8990112233445566778899aabbccddeeff00112233445566"
            """));
    }

    [Fact]
    public void Describe_UrlWithUsernameButNoPassword_IsNotFlagged()
    {
        Assert.Null(SecretScanner.Describe("git = \"https://git@gitlab.example.com/kani/addon.git\""));
    }
}
