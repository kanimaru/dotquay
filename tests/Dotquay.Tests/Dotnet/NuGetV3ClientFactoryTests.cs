using Dotquay.Core.Dotnet;
using Xunit;

namespace Dotquay.Tests.Dotnet;

/// <summary>
/// Covers <see cref="NuGetV3ClientFactory"/>'s two jobs: reading
/// <c>DOTQUAY_NUGET_USERNAME</c>/<c>DOTQUAY_NUGET_TOKEN</c> only for non-default sources, and
/// caching one client per source URL. Mutates process environment variables directly (there's no
/// seam for injecting them) — each test sets up and tears down its own, and xUnit runs facts within
/// one class sequentially by default, so this doesn't race against itself.
/// </summary>
public class NuGetV3ClientFactoryTests
{
    private const string UsernameVar = "DOTQUAY_NUGET_USERNAME";
    private const string TokenVar = "DOTQUAY_NUGET_TOKEN";

    private static void WithEnvironment(string? username, string? token, Action action)
    {
        var previousUsername = Environment.GetEnvironmentVariable(UsernameVar);
        var previousToken = Environment.GetEnvironmentVariable(TokenVar);
        try
        {
            Environment.SetEnvironmentVariable(UsernameVar, username);
            Environment.SetEnvironmentVariable(TokenVar, token);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(UsernameVar, previousUsername);
            Environment.SetEnvironmentVariable(TokenVar, previousToken);
        }
    }

    [Fact]
    public void Create_NoSourceGiven_ReturnsClientForDefaultNuGetOrg()
    {
        var factory = new NuGetV3ClientFactory();

        var client = factory.Create(null);

        Assert.IsType<NuGetV3Client>(client);
    }

    [Fact]
    public void Create_SameSourceTwice_ReturnsTheSameCachedInstance()
    {
        var factory = new NuGetV3ClientFactory();

        var first = factory.Create("https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json");
        var second = factory.Create("https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json");

        Assert.Same(first, second);
    }

    [Fact]
    public void Create_DifferentSources_ReturnsDistinctInstances()
    {
        var factory = new NuGetV3ClientFactory();

        var a = factory.Create("https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json");
        var b = factory.Create("https://gitlab.example.com/api/v4/projects/2/packages/nuget/index.json");

        Assert.NotSame(a, b);
    }

    [Fact]
    public void Create_NullAndDefaultUrlAreTheSameSource()
    {
        var factory = new NuGetV3ClientFactory();

        var viaNull = factory.Create(null);
        var viaExplicitDefault = factory.Create(NuGetV3Client.DefaultServiceIndexUrl);

        Assert.Same(viaNull, viaExplicitDefault);
    }

    [Fact]
    public void Create_CustomSource_DoesNotThrowEvenWithoutCredentialsConfigured()
    {
        WithEnvironment(username: null, token: null, () =>
        {
            var factory = new NuGetV3ClientFactory();

            var client = factory.Create("https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json");

            Assert.IsType<NuGetV3Client>(client); // credential absence is only a problem once a request is made
        });
    }
}
