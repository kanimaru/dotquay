using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Manifest;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet;

/// <summary>
/// Covers ordered multi-feed search: first match wins, a miss falls through, and a feed that refuses
/// to answer stops the search rather than deferring to a public one.
/// </summary>
public class MultiSourceNuGetClientTests
{
    private static MultiSourceNuGetClient Build(params (string Name, FakeNuGetClient Client)[] feeds) =>
        new(feeds
            .Select(feed => (new NuGetSourceSpec(feed.Name, $"https://{feed.Name}.example.com/index.json"), (INuGetClient)feed.Client))
            .ToList());

    [Fact]
    public async Task GetVersionsAsync_FirstFeedWithThePackageWins_EvenIfALaterOneHasHigherVersions()
    {
        var client = Build(
            ("core", new FakeNuGetClient("core").With("Acme.Internal", "1.0.0")),
            ("nuget.org", new FakeNuGetClient().With("Acme.Internal", "99.0.0")));

        var query = await client.GetVersionsAsync("Acme.Internal");

        // Taking the highest version wherever it lives is a dependency-confusion substitution attack:
        // publishing Acme.Internal 99.0.0 publicly would silently redirect every consumer.
        Assert.Equal(["1.0.0"], query.Versions);
        Assert.Equal("core", query.SourceName);
    }

    [Fact]
    public async Task GetVersionsAsync_PackageMissingFromTheFirstFeed_FallsThroughToTheNext()
    {
        var client = Build(
            ("core", new FakeNuGetClient("core")),
            ("tools", new FakeNuGetClient("tools").With("Acme.Internal", "2.0.0")));

        var query = await client.GetVersionsAsync("Acme.Internal");

        Assert.Equal(["2.0.0"], query.Versions);
        Assert.Equal("tools", query.SourceName);
    }

    [Fact]
    public async Task GetVersionsAsync_FoundNowhere_ReturnsEmpty()
    {
        var client = Build(("core", new FakeNuGetClient("core")), ("tools", new FakeNuGetClient("tools")));

        Assert.False((await client.GetVersionsAsync("Acme.Internal")).Found);
    }

    [Fact]
    public async Task GetVersionsAsync_AuthFailure_PropagatesInsteadOfFallingThrough()
    {
        var unauthorized = new FakeNuGetClient("core");
        unauthorized.UnauthorizedPackageIds.Add("Acme.Internal");

        var client = Build(
            ("core", unauthorized),
            ("nuget.org", new FakeNuGetClient().With("Acme.Internal", "99.0.0")));

        // "This feed might have it and won't say" must stop the search. Deferring to the public feed
        // here is the exact substitution ordered resolution exists to prevent.
        await Assert.ThrowsAsync<NuGetAuthenticationException>(() => client.GetVersionsAsync("Acme.Internal"));
    }

    [Fact]
    public async Task GetVersionsAsync_PackageOnSeveralFeeds_WarnsAndStillUsesTheFirst()
    {
        var client = Build(
            ("core", new FakeNuGetClient("core").With("Acme.Internal", "1.0.0")),
            ("tools", new FakeNuGetClient("tools").With("Acme.Internal", "1.5.0")));

        var query = await client.GetVersionsAsync("Acme.Internal");

        Assert.Equal("core", query.SourceName);

        // Not an error — a private feed legitimately mirrors public packages — but the two copies can
        // differ and only the first is ever used, which is worth saying.
        var warning = Assert.Single(client.Warnings);
        Assert.Contains("core", warning, StringComparison.Ordinal);
        Assert.Contains("tools", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetVersionsAsync_SingleFeed_ProducesNoWarning()
    {
        var client = Build(("core", new FakeNuGetClient("core").With("Acme.Internal", "1.0.0")));

        await client.GetVersionsAsync("Acme.Internal");

        Assert.Empty(client.Warnings);
    }

    [Fact]
    public async Task PackageExistsAsync_MirrorsGetVersions()
    {
        var client = Build(("core", new FakeNuGetClient("core").With("Acme.Internal", "1.0.0")));

        Assert.True(await client.PackageExistsAsync("Acme.Internal"));
        Assert.False(await client.PackageExistsAsync("Nope"));
    }
}
