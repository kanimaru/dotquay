using Dotquay.Core.Git;
using Xunit;

namespace Dotquay.Tests.Git;

/// <summary>
/// Covers the inference in <see cref="GitTransportPreference"/>: reading a project's own git URLs as
/// a statement about how this machine reaches each host, then applying it to URLs the project did
/// not write.
/// </summary>
public class GitTransportPreferenceTests {
    private static GitTransportPreference From(params string?[] urls) =>
        GitTransportPreference.FromProjectUrls(urls);

    private static string Rewrite(GitTransportPreference preference, string url) {
        preference.TryRewrite(url, out var rewritten);
        return rewritten;
    }

    [Fact]
    public void HttpsProject_RewritesAnSshDependencyOnTheSameHost() {
        // The incident, end to end: every URL the project wrote is HTTPS, and a dependency it never
        // named arrives as SSH.
        var preference = From("https://gitlab.kemomi.de/wild-alliance/vbeami");

        Assert.True(preference.TryRewrite("git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git", out var rewritten));
        Assert.Equal("https://gitlab.kemomi.de/wild-alliance/mtoon-shader", rewritten);
    }

    [Fact]
    public void SshProject_RewritesAnHttpsDependencyOnTheSameHost() {
        // The mirror case matters as much: an SSH-only shop inheriting HTTPS URLs gets credential
        // prompts where their keys would have worked silently.
        var preference = From("git@gitlab.kemomi.de:wild-alliance/vbeami.git");

        Assert.True(preference.TryRewrite("https://gitlab.kemomi.de/wild-alliance/mtoon-shader", out var rewritten));
        Assert.Equal("git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git", rewritten);
    }

    [Fact]
    public void UnknownHost_IsLeftAlone() {
        // The project has said nothing about github.com. Guessing from an unrelated host would send
        // a fetch somewhere the user never described.
        var preference = From("https://gitlab.kemomi.de/g/r");

        Assert.False(preference.TryRewrite("git@github.com:bitwes/Gut.git", out var rewritten));
        Assert.Equal("git@github.com:bitwes/Gut.git", rewritten);
    }

    [Fact]
    public void AlreadyMatchingTransport_ReportsNoChange() {
        // False means "nothing worth telling the user about", so a caller never has to re-compare.
        var preference = From("https://host/g/a");

        Assert.False(preference.TryRewrite("https://host/g/b", out _));
    }

    [Fact]
    public void HostDescribedTwoWays_IsAmbiguousAndRewritesNothing() {
        // A mirror, or a read-only HTTPS remote beside a push-capable SSH one. Both are deliberate
        // setups, and picking a winner by majority would invent an answer the project did not give.
        var preference = From("https://host/g/a", "git@host:g/b.git");

        Assert.False(preference.TryRewrite("git@host:g/c.git", out _));
        Assert.Contains("host", preference.AmbiguousHosts);
    }

    [Fact]
    public void AmbiguityOnOneHost_DoesNotDisableAnother() {
        var preference = From("https://mixed/g/a", "git@mixed:g/b.git", "https://clean/g/c");

        Assert.False(preference.TryRewrite("git@mixed:g/d.git", out _));
        Assert.True(preference.TryRewrite("git@clean:g/e.git", out _));
    }

    [Fact]
    public void FirstDeclarationWins_ForSpellingAmongAgreeingUrls() {
        // Same transport, different house style. Deterministic beats clever: the result depends only
        // on the manifest, in declaration order.
        var preference = From("https://host/g/a.git", "https://host/g/b");

        Assert.Equal("https://host/g/c.git", Rewrite(preference, "git@host:g/c"));
    }

    [Fact]
    public void SameTransportDifferentPorts_IsAmbiguous() {
        // Two SSH routes onto one host is still two answers to "how do I reach it".
        var preference = From("ssh://git@host/g/a", "ssh://git@host:2222/g/b");

        Assert.Contains("host", preference.AmbiguousHosts);
        Assert.False(preference.TryRewrite("https://host/g/c", out _));
    }

    [Theory]
    [InlineData(@"C:\repos\vbeami")]
    [InlineData("./vendor/vbeami")]
    public void LocalPaths_AreNotEvidence(string localUrl) {
        // A folder on this disk says nothing about reaching a remote, and has no host to key on.
        var preference = From(localUrl);

        Assert.Same(GitTransportPreference.None, preference);
    }

    [Fact]
    public void UnparseableDeclarations_AreIgnoredRatherThanFatal() {
        // A manifest can contain anything; resolution reports a bad URL properly elsewhere. Inference
        // is a best-effort convenience and must never be the thing that fails a command.
        var preference = From("ftp://nope", null, "", "https://host/g/a");

        Assert.True(preference.TryRewrite("git@host:g/b.git", out _));
    }

    [Fact]
    public void NoDeclarations_RewritesNothing() {
        Assert.Same(GitTransportPreference.None, From());
        Assert.False(GitTransportPreference.None.TryRewrite("git@host:g/r.git", out _));
    }

    [Fact]
    public void TryRewrite_UnparseableInput_LeavesItUntouched() {
        var preference = From("https://host/g/a");

        Assert.False(preference.TryRewrite("ftp://host/g/b", out var rewritten));
        Assert.Equal("ftp://host/g/b", rewritten);
    }
}
