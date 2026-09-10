using Dotquay.Core.Git;
using Xunit;

namespace Dotquay.Tests.Git;

/// <summary>
/// Covers <see cref="GitUrl"/>: parsing git's two URL grammars, deciding when two spellings name one
/// repository, and re-expressing a repository over another transport.
/// </summary>
/// <remarks>
/// Worth a dense suite because every consumer of this type makes an irreversible-looking decision
/// from it. Comparing too loosely unifies two repositories into one install folder; comparing too
/// strictly reports a conflict on a project that is fine; rewriting too eagerly sends a fetch at a
/// host the user never named.
/// </remarks>
public class GitUrlTests
{
    // -----------------------------------------------------------------------------------------
    // Parsing: the scp-like form
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Parse_ScpLike_SplitsUserHostAndPath()
    {
        var url = GitUrl.Parse("git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git");

        Assert.Equal(GitTransport.Ssh, url.Transport);
        Assert.Equal("git", url.User);
        Assert.Equal("gitlab.kemomi.de", url.Host);
        Assert.Equal("wild-alliance/mtoon-shader", url.Path);
        Assert.True(url.HadGitSuffix);
        Assert.True(url.ScpLike);
        Assert.Null(url.Port);
    }

    [Fact]
    public void Parse_ScpLike_ColonDoesNotIntroduceAPort()
    {
        // git reads the scp-like colon as the path separator, so this is the path "2222/x" and not
        // port 2222. Matching git matters more than being intuitive: dotquay hands these strings
        // straight back to git.
        var url = GitUrl.Parse("git@host:2222/x");

        Assert.Null(url.Port);
        Assert.Equal("2222/x", url.Path);
    }

    // -----------------------------------------------------------------------------------------
    // Parsing: schemed URLs
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Parse_Https_TrailingSlashAndSuffixAreNotPartOfThePath()
    {
        var url = GitUrl.Parse("https://gitlab.kemomi.de/wild-alliance/vbeami/");

        Assert.Equal(GitTransport.Https, url.Transport);
        Assert.Equal("wild-alliance/vbeami", url.Path);
        Assert.False(url.HadGitSuffix);
    }

    [Theory]
    [InlineData("ssh://git@host/x", GitTransport.Ssh)]
    [InlineData("git+ssh://git@host/x", GitTransport.Ssh)]
    [InlineData("http://host/x", GitTransport.Http)]
    [InlineData("git://host/x", GitTransport.Daemon)]
    public void Parse_RecognisedSchemes(string raw, GitTransport expected) =>
        Assert.Equal(expected, GitUrl.Parse(raw).Transport);

    [Theory]
    [InlineData("ftp://host/x")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_Unusable_ReturnsFalse(string? raw) =>
        Assert.False(GitUrl.TryParse(raw, out _));

    [Fact]
    public void Parse_DefaultPort_IsDiscarded()
    {
        // Otherwise "ssh://host:22/x" and "git@host:x" -- the same thing said two ways -- would
        // report as different repositories.
        Assert.Null(GitUrl.Parse("ssh://host:22/x").Port);
        Assert.Null(GitUrl.Parse("https://host:443/x").Port);
        Assert.Equal(2222, GitUrl.Parse("ssh://host:2222/x").Port);
    }

    [Fact]
    public void Parse_AuthorityWithEmbeddedAt_SplitsOnTheLastOne()
    {
        var url = GitUrl.Parse("ssh://user@example.com@host/x");

        Assert.Equal("user@example.com", url.User);
        Assert.Equal("host", url.Host);
    }

    // -----------------------------------------------------------------------------------------
    // Parsing: local paths, and the drive-letter ambiguity the scp-like form creates
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\repos\vbeami")]
    [InlineData("C:/repos/vbeami")]
    [InlineData("./vendor/vbeami")]
    [InlineData("/home/kani/repos/vbeami")]
    [InlineData("file:///home/kani/repos/vbeami")]
    public void Parse_LocalPaths(string raw) =>
        Assert.Equal(GitTransport.Local, GitUrl.Parse(raw).Transport);

    [Fact]
    public void Parse_SingleLetterHostWithoutASeparator_IsStillAHost()
    {
        // The drive-letter rule keys on the separator, not on length alone: "c:x/y" is a valid
        // scp-like URL for a host literally named "c", and a drive reference always has a separator.
        var url = GitUrl.Parse("c:x/y");

        Assert.Equal(GitTransport.Ssh, url.Transport);
        Assert.Equal("c", url.Host);
    }

    // -----------------------------------------------------------------------------------------
    // Identity
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SameRepositoryAs_AcrossTransports_IsTrue()
    {
        // The whole point. These two strings are what the incident was made of.
        var https = GitUrl.Parse("https://gitlab.kemomi.de/wild-alliance/mtoon-shader");
        var ssh = GitUrl.Parse("git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git");

        Assert.True(https.SameRepositoryAs(ssh));
        Assert.True(ssh.SameRepositoryAs(https));
    }

    [Fact]
    public void SameRepositoryAs_HostCasing_IsIgnored() =>
        Assert.True(GitUrl.Parse("https://GitLab.Kemomi.DE/g/r").SameRepositoryAs(GitUrl.Parse("https://gitlab.kemomi.de/g/r")));

    [Fact]
    public void SameRepositoryAs_PathCasing_IsNotIgnored()
    {
        // Stricter than GitHub and GitLab need, on purpose: a self-hosted server on a case-sensitive
        // filesystem really does serve two repositories here. Over-reporting costs one [overrides]
        // line; under-reporting silently merges two addons into one folder.
        Assert.False(GitUrl.Parse("https://host/g/Repo").SameRepositoryAs(GitUrl.Parse("https://host/g/repo")));
    }

    [Fact]
    public void SameRepositoryAs_ExplicitNonDefaultPort_IsPartOfIdentity() =>
        Assert.False(GitUrl.Parse("ssh://host:2222/g/r").SameRepositoryAs(GitUrl.Parse("git@host:g/r")));

    [Fact]
    public void SameRepositoryAs_LocalPathAndRemote_IsNeverTrue()
    {
        // A checkout of a repository is not that repository. Treating them as one would let a graph
        // walk substitute somebody's working copy for a pinned dependency.
        var local = GitUrl.Parse(@"C:\repos\mtoon-shader");
        var remote = GitUrl.Parse("https://host/g/mtoon-shader");

        Assert.False(local.SameRepositoryAs(remote));
        Assert.False(remote.SameRepositoryAs(local));
    }

    // -----------------------------------------------------------------------------------------
    // Transport rewriting
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void WithTransportOf_SshToHttps_KeepsTheRepositoryAndDropsTheUser()
    {
        var declared = GitUrl.Parse("git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git");
        var template = GitUrl.Parse("https://gitlab.kemomi.de/wild-alliance/vbeami");

        var rewritten = declared.WithTransportOf(template);

        Assert.Equal("https://gitlab.kemomi.de/wild-alliance/mtoon-shader", rewritten.Original);
        Assert.True(rewritten.SameRepositoryAs(declared));
    }

    [Fact]
    public void WithTransportOf_MirrorsTheTemplatesGitSuffixConvention()
    {
        var declared = GitUrl.Parse("git@host:g/r.git");

        Assert.Equal("https://host/g/r.git", declared.WithTransportOf(GitUrl.Parse("https://host/g/other.git")).Original);
        Assert.Equal("https://host/g/r", declared.WithTransportOf(GitUrl.Parse("https://host/g/other")).Original);
    }

    [Fact]
    public void WithTransportOf_HttpsToSsh_MirrorsTheTemplatesSpelling()
    {
        var declared = GitUrl.Parse("https://host/g/r");

        Assert.Equal("git@host:g/r.git", declared.WithTransportOf(GitUrl.Parse("git@host:g/other.git")).Original);
        Assert.Equal("ssh://git@host/g/r", declared.WithTransportOf(GitUrl.Parse("ssh://git@host/g/other")).Original);
    }

    [Fact]
    public void WithTransportOf_NeverWritesAUserIntoAnHttpsUrl()
    {
        // git would read it as the credential identity for that remote, changing which stored
        // credential matches. A transport rewrite has no business deciding that.
        var rewritten = GitUrl.Parse("ssh://deploy@host/g/r").WithTransportOf(GitUrl.Parse("https://host/g/other"));

        Assert.DoesNotContain("@", rewritten.Original, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTransportOf_DifferentHost_IsRefused()
    {
        // Keys, credentials and firewall rules are per-host. Nothing about reaching host A is
        // evidence about host B.
        var declared = GitUrl.Parse("git@other-host:g/r.git");

        Assert.Same(declared, declared.WithTransportOf(GitUrl.Parse("https://host/g/x")));
    }

    [Fact]
    public void WithTransportOf_TemplateWithExplicitPort_IsRefused()
    {
        // The port belongs to the template's own route onto that host, not necessarily to this
        // repository -- an SSH gateway on 2222 does not imply every repo is reachable through it.
        var declared = GitUrl.Parse("https://host/g/r");

        Assert.Same(declared, declared.WithTransportOf(GitUrl.Parse("ssh://git@host:2222/g/x")));
    }

    [Fact]
    public void WithTransportOf_LocalOnEitherSide_IsRefused()
    {
        var local = GitUrl.Parse(@"C:\repos\r");
        var remote = GitUrl.Parse("https://host/g/r");

        Assert.Same(local, local.WithTransportOf(remote));
        Assert.Same(remote, remote.WithTransportOf(local));
    }

    [Fact]
    public void WithTransportOf_SameTransport_ReturnsTheSameInstance()
    {
        // Not just equal: identical, so a caller can use reference equality to mean "nothing to
        // report" without comparing strings.
        var declared = GitUrl.Parse("https://host/g/r");

        Assert.Same(declared, declared.WithTransportOf(GitUrl.Parse("https://host/g/other.git")));
    }

    [Fact]
    public void ToString_RoundTripsWhatTheUserWrote() =>
        Assert.Equal("git@host:g/r.git", GitUrl.Parse("git@host:g/r.git").ToString());
}
