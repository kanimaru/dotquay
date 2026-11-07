using Dotquay.Core.Auth;
using Xunit;

namespace Dotquay.Tests.Auth;

/// <summary>Covers git's credential helper wire format, parsed and formatted without a process or a
/// pipe in sight.</summary>
public class GitCredentialProtocolTests {
    [Fact]
    public void ParseRequest_ReadsKeyValueLinesUpToTheBlankLine() {
        var request = GitCredentialProtocol.ParseRequest("protocol=https\nhost=gitlab.example.com\n\nignored=yes\n");

        Assert.Equal("https", request["protocol"]);
        Assert.Equal("gitlab.example.com", request["host"]);
        Assert.False(request.ContainsKey("ignored"));
    }

    [Fact]
    public void ParseRequest_ToleratesCarriageReturnsAndUnknownKeys() {
        // git adds fields over time (wwwauth[], capability[]); a helper that choked on one it didn't
        // recognise would break on a git upgrade.
        var request = GitCredentialProtocol.ParseRequest("protocol=https\r\nhost=example.com\r\nwwwauth[]=Basic\r\n\r\n");

        Assert.Equal("example.com", request["host"]);
        Assert.Equal("Basic", request["wwwauth[]"]);
    }

    [Fact]
    public void ParseRequest_ValueContainingEquals_KeepsTheWholeValue() {
        var request = GitCredentialProtocol.ParseRequest("url=https://example.com/a?b=c\n\n");

        Assert.Equal("https://example.com/a?b=c", request["url"]);
    }

    [Fact]
    public void BuildUrl_FromProtocolAndHost() {
        var url = GitCredentialProtocol.BuildUrl(new Dictionary<string, string> {
            ["protocol"] = "https",
            ["host"] = "gitlab.example.com",
        });

        Assert.Equal("https://gitlab.example.com", url);
    }

    [Fact]
    public void BuildUrl_IncludesPathWhenGitSendsOne() {
        var url = GitCredentialProtocol.BuildUrl(new Dictionary<string, string> {
            ["protocol"] = "https",
            ["host"] = "gitlab.example.com",
            ["path"] = "kani/addon.git",
        });

        Assert.Equal("https://gitlab.example.com/kani/addon.git", url);
    }

    [Fact]
    public void BuildUrl_ExplicitUrlKeyWins() {
        var url = GitCredentialProtocol.BuildUrl(new Dictionary<string, string> {
            ["url"] = "https://other.example.com/x",
            ["host"] = "gitlab.example.com",
        });

        Assert.Equal("https://other.example.com/x", url);
    }

    [Fact]
    public void BuildUrl_NoHost_ReturnsNull() {
        Assert.Null(GitCredentialProtocol.BuildUrl(new Dictionary<string, string> { ["protocol"] = "https" }));
    }

    [Fact]
    public void FormatResponse_EndsWithABlankLine() {
        var response = GitCredentialProtocol.FormatResponse(new Credential("kani", "glpat-abc"));

        // git reads until a blank line; omitting it leaves git waiting on a pipe forever.
        Assert.Equal("username=kani\npassword=glpat-abc\n\n", response);
    }

    [Fact]
    public void FormatResponse_StripsNewlinesFromValues() {
        // The delimiter is a newline and the values come from a user-editable file, so an embedded
        // newline would inject a protocol field rather than merely render oddly.
        var response = GitCredentialProtocol.FormatResponse(new Credential("kani", "abc\nquit=1"));

        Assert.Equal("username=kani\npassword=abcquit=1\n\n", response);
    }
}
