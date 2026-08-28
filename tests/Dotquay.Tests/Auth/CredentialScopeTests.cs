using Dotquay.Core.Auth;
using Xunit;

namespace Dotquay.Tests.Auth;

public class CredentialScopeTests
{
    [Fact]
    public void Parse_BareHost_HasNoPathAndIsAssumedSecure()
    {
        var scope = CredentialScope.Parse("gitlab.example.com");

        Assert.Equal("gitlab.example.com", scope.Host);
        Assert.Equal(string.Empty, scope.Path);
        Assert.True(scope.IsSecure);
        Assert.Equal("gitlab.example.com", scope.Key);
    }

    [Fact]
    public void Parse_FullUrl_KeepsPathWithoutSurroundingSlashes()
    {
        var scope = CredentialScope.Parse("https://gitlab.example.com/api/v4/projects/123/packages/nuget/index.json");

        Assert.Equal("gitlab.example.com", scope.Host);
        Assert.Equal("api/v4/projects/123/packages/nuget/index.json", scope.Path);
    }

    [Fact]
    public void Parse_HostIsLowercasedButPathIsNot()
    {
        var scope = CredentialScope.Parse("https://GitLab.Example.COM/Kani/Addon");

        Assert.Equal("gitlab.example.com", scope.Host);
        Assert.Equal("Kani/Addon", scope.Path);
    }

    [Fact]
    public void Parse_DefaultPortIsOmittedButExplicitOneIsKept()
    {
        Assert.Equal("example.com", CredentialScope.Parse("https://example.com:443/x").Host);
        Assert.Equal("example.com:8443", CredentialScope.Parse("https://example.com:8443/x").Host);
    }

    [Fact]
    public void Parse_PlainHttpIsNotSecure_ButLoopbackIs()
    {
        Assert.False(CredentialScope.Parse("http://gitlab.example.com").IsSecure);
        Assert.True(CredentialScope.Parse("http://localhost:5000/feed").IsSecure);
        Assert.True(CredentialScope.Parse("http://127.0.0.1:5000/feed").IsSecure);
    }

    [Theory]
    [InlineData("ssh://git@gitlab.example.com/kani/addon.git")]
    [InlineData("git://example.com/repo.git")]
    public void TryParse_NonHttpSchemes_AreRejectedWithAnExplanation(string url)
    {
        Assert.False(CredentialScope.TryParse(url, out _, out var error));
        Assert.Contains("SSH", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_Blank_IsRejected(string input)
    {
        Assert.False(CredentialScope.TryParse(input, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Covers_HostWideScope_CoversEveryPathOnThatHost()
    {
        var host = CredentialScope.Parse("gitlab.example.com");

        Assert.True(host.Covers(CredentialScope.Parse("https://gitlab.example.com/api/v4/projects/1")));
        Assert.True(host.Covers(CredentialScope.Parse("gitlab.example.com")));
    }

    [Fact]
    public void Covers_DifferentHost_IsNeverCovered()
    {
        var host = CredentialScope.Parse("gitlab.example.com");

        Assert.False(host.Covers(CredentialScope.Parse("https://github.com/kani/addon")));
        Assert.False(host.Covers(CredentialScope.Parse("https://gitlab.example.com:8443/x")));
    }

    [Fact]
    public void Covers_PathPrefixOnlyMatchesAtASegmentBoundary()
    {
        var scope = CredentialScope.Parse("gitlab.example.com/api/v4");

        Assert.True(scope.Covers(CredentialScope.Parse("https://gitlab.example.com/api/v4")));
        Assert.True(scope.Covers(CredentialScope.Parse("https://gitlab.example.com/api/v4/projects/1")));

        // "v41" starts with "v4" as text, and must not match — this is the whole reason Covers
        // compares on segment boundaries instead of calling StartsWith.
        Assert.False(scope.Covers(CredentialScope.Parse("https://gitlab.example.com/api/v41/projects/1")));
        Assert.False(scope.Covers(CredentialScope.Parse("https://gitlab.example.com/api")));
    }

    [Fact]
    public void Specificity_OrdersHostWideBelowPathScoped()
    {
        Assert.True(
            CredentialScope.Parse("gitlab.example.com/api/v4/projects/123").Specificity
            > CredentialScope.Parse("gitlab.example.com").Specificity);
    }
}
