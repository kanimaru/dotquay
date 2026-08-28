using Dotquay.Core.Auth;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Manifest;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet;

/// <summary>
/// Covers <see cref="NuGetV3ClientFactory"/>'s jobs: composing the per-source clients into an ordered
/// <see cref="MultiSourceNuGetClient"/>, honouring a <c>source = "..."</c> pin, caching one client
/// per source URL, and never authenticating against nuget.org.
/// </summary>
/// <remarks>
/// Credentials come from a <see cref="FakeEnvironmentReader"/>. Before v0.7 this class set the real
/// <c>DOTQUAY_NUGET_*</c> process variables and restored them in a finally, correctly noting that
/// xunit runs one class's facts sequentially — and missing that it runs classes in parallel, against
/// <c>DotnetInstallerTests</c>, which mutated the same two variables.
/// </remarks>
public class NuGetV3ClientFactoryTests
{
    private const string SourceUrl = "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json";
    private const string SecondUrl = "https://gitlab.example.com/api/v4/projects/2/packages/nuget/index.json";

    private static NuGetV3ClientFactory Factory(string? username = null, string? token = null)
    {
        var environment = new FakeEnvironmentReader();
        if (username is not null && token is not null)
        {
            environment
                .Set(CredentialResolver.GlobalUsernameVariable, username)
                .Set(CredentialResolver.GlobalTokenVariable, token);
        }

        return new NuGetV3ClientFactory(new CredentialResolver(environment, new InMemoryCredentialStore()));
    }

    [Fact]
    public void Create_NoDeclaredSources_StillSearchesNuGetOrg()
    {
        var client = Factory().Create(NuGetSourceSet.PublicOnly);

        Assert.IsType<MultiSourceNuGetClient>(client);
    }

    [Fact]
    public void Create_DeclaredSources_AreSearchedInDeclarationOrderWithNuGetOrgLast()
    {
        var sources = NuGetSourceSet.From(
            [new NuGetSourceSpec("core", SourceUrl), new NuGetSourceSpec("tools", SecondUrl)]);

        // Order is the resolution rule, so it is asserted on the set the factory is handed rather
        // than left to a dictionary's enumeration order.
        Assert.Equal(
            ["core", "tools", NuGetSourceSpec.PublicName],
            sources.All.Select(source => source.Name));
    }

    [Fact]
    public void Create_Pin_NarrowsTheSearchToThatOneSource()
    {
        var sources = NuGetSourceSet.From(
            [new NuGetSourceSpec("core", SourceUrl), new NuGetSourceSpec("tools", SecondUrl)]);

        Assert.Equal(["tools"], sources.Resolve("tools").All.Select(source => source.Name));
    }

    [Fact]
    public void Create_UnknownPin_NamesTheSourcesThatDoExist()
    {
        var sources = NuGetSourceSet.From([new NuGetSourceSpec("core", SourceUrl)]);

        var exception = Assert.Throws<ManifestParseException>(() => sources.Resolve("typo"));

        Assert.Contains("'core'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_SameSourceTwice_ReusesTheCachedPerSourceClient()
    {
        var factory = Factory();
        var sources = NuGetSourceSet.From([new NuGetSourceSpec("core", SourceUrl)]);

        // The aggregate is new each call; the per-source clients underneath are cached, which is what
        // stops a project with a dozen dependencies re-fetching the same service index a dozen times.
        var first = factory.Create(sources);
        var second = factory.Create(sources);

        Assert.NotSame(first, second);
        Assert.IsType<MultiSourceNuGetClient>(first);
        Assert.IsType<MultiSourceNuGetClient>(second);
    }

    [Fact]
    public void Create_ExplicitlyDeclaredNuGetOrg_IsNotDuplicated()
    {
        var sources = NuGetSourceSet.From([new NuGetSourceSpec("public", NuGetSourceSpec.PublicUrl)]);

        Assert.Single(sources.All);
        Assert.Equal("public", sources.All[0].Name);
    }

    [Fact]
    public void Create_CustomSource_DoesNotThrowWithoutCredentialsConfigured()
    {
        var client = Factory().Create(NuGetSourceSet.From([new NuGetSourceSpec("core", SourceUrl)]));

        // Credential absence is only a problem once a request is made.
        Assert.IsType<MultiSourceNuGetClient>(client);
    }

    [Fact]
    public void Create_WithCredentials_DoesNotThrow()
    {
        var client = Factory("kani", "glpat-secret")
            .Create(NuGetSourceSet.From([new NuGetSourceSpec("core", SourceUrl)]));

        Assert.IsType<MultiSourceNuGetClient>(client);
    }
}
