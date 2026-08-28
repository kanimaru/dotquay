using Dotquay.Core.Auth;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Auth;

public class CredentialResolverTests
{
    private const string SourceName = "gitlab-core";
    private const string SourceUrl = "https://gitlab.example.com/api/v4/projects/123/packages/nuget/index.json";

    [Fact]
    public void SourceVariableNames_NormalisesToAnExportableShellName()
    {
        var (username, token) = CredentialResolver.SourceVariableNames("gitlab-core.v2");

        Assert.Equal("DOTQUAY_NUGET_USERNAME_GITLAB_CORE_V2", username);
        Assert.Equal("DOTQUAY_NUGET_TOKEN_GITLAB_CORE_V2", token);
    }

    [Fact]
    public void ForNuGetSource_NothingConfigured_IsAnonymous()
    {
        var resolver = new CredentialResolver(new FakeEnvironmentReader(), new InMemoryCredentialStore());

        var lookup = resolver.ForNuGetSource(SourceName, SourceUrl);

        Assert.Null(lookup.Credential);
        Assert.Equal(CredentialOrigin.None, lookup.Origin);
    }

    [Fact]
    public void ForNuGetSource_StoredCredential_IsUsedWhenNoEnvironmentIsSet()
    {
        var store = new InMemoryCredentialStore().Add("gitlab.example.com", "kani", "from-file");
        var resolver = new CredentialResolver(new FakeEnvironmentReader(), store);

        var lookup = resolver.ForNuGetSource(SourceName, SourceUrl);

        Assert.Equal("from-file", lookup.Credential!.Token);
        Assert.Equal(CredentialOrigin.CredentialFile, lookup.Origin);
    }

    [Fact]
    public void ForNuGetSource_GlobalEnvironmentShadowsTheStoredCredential()
    {
        var environment = new FakeEnvironmentReader()
            .Set(CredentialResolver.GlobalUsernameVariable, "ci")
            .Set(CredentialResolver.GlobalTokenVariable, "from-global-env");
        var store = new InMemoryCredentialStore().Add("gitlab.example.com", "kani", "from-file");

        var lookup = new CredentialResolver(environment, store).ForNuGetSource(SourceName, SourceUrl);

        Assert.Equal("from-global-env", lookup.Credential!.Token);
        Assert.Equal(CredentialOrigin.GlobalEnvironment, lookup.Origin);
    }

    [Fact]
    public void ForNuGetSource_PerSourceEnvironmentShadowsTheGlobalPair()
    {
        var (usernameVariable, tokenVariable) = CredentialResolver.SourceVariableNames(SourceName);
        var environment = new FakeEnvironmentReader()
            .Set(CredentialResolver.GlobalUsernameVariable, "ci")
            .Set(CredentialResolver.GlobalTokenVariable, "from-global-env")
            .Set(usernameVariable, "kani")
            .Set(tokenVariable, "from-source-env");
        var store = new InMemoryCredentialStore().Add("gitlab.example.com", "kani", "from-file");

        var lookup = new CredentialResolver(environment, store).ForNuGetSource(SourceName, SourceUrl);

        Assert.Equal("from-source-env", lookup.Credential!.Token);
        Assert.Equal(CredentialOrigin.SourceEnvironment, lookup.Origin);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ForNuGetSource_HalfConfiguredEnvironmentPair_FallsThroughToTheStore(bool setUsername, bool setToken)
    {
        var environment = new FakeEnvironmentReader();
        if (setUsername)
        {
            environment.Set(CredentialResolver.GlobalUsernameVariable, "ci");
        }

        if (setToken)
        {
            environment.Set(CredentialResolver.GlobalTokenVariable, "from-global-env");
        }

        var store = new InMemoryCredentialStore().Add("gitlab.example.com", "kani", "from-file");

        var lookup = new CredentialResolver(environment, store).ForNuGetSource(SourceName, SourceUrl);

        Assert.Equal("from-file", lookup.Credential!.Token);
        Assert.Equal(CredentialOrigin.CredentialFile, lookup.Origin);
    }

    [Fact]
    public void ForNuGetSource_PlainHttp_WithholdsTheCredentialAndSaysSo()
    {
        var store = new InMemoryCredentialStore().Add("gitlab.example.com", "kani", "from-file");
        var resolver = new CredentialResolver(new FakeEnvironmentReader(), store);

        var lookup = resolver.ForNuGetSource(SourceName, "http://gitlab.example.com/api/v4/index.json");

        Assert.Null(lookup.Credential);
        Assert.Equal(CredentialOrigin.WithheldInsecureTransport, lookup.Origin);
    }

    [Fact]
    public void ForNuGetSource_PlainHttpLoopback_IsAllowed()
    {
        var store = new InMemoryCredentialStore().Add("http://localhost:5000", "kani", "local");
        var resolver = new CredentialResolver(new FakeEnvironmentReader(), store);

        var lookup = resolver.ForNuGetSource(SourceName, "http://localhost:5000/v3/index.json");

        Assert.Equal("local", lookup.Credential!.Token);
    }

    [Fact]
    public void ForGitRepository_UsesTheStore_AndIgnoresTheNuGetEnvironmentVariables()
    {
        var environment = new FakeEnvironmentReader()
            .Set(CredentialResolver.GlobalUsernameVariable, "ci")
            .Set(CredentialResolver.GlobalTokenVariable, "nuget-only");
        var store = new InMemoryCredentialStore().Add("gitlab.example.com", "kani", "from-file");

        var lookup = new CredentialResolver(environment, store)
            .ForGitRepository("https://gitlab.example.com/kani/addon.git");

        Assert.Equal("from-file", lookup.Credential!.Token);
        Assert.Equal(CredentialOrigin.CredentialFile, lookup.Origin);
    }

    [Fact]
    public void ForGitRepository_SshRemote_IsAnonymousRatherThanAnError()
    {
        var store = new InMemoryCredentialStore().Add("gitlab.example.com", "kani", "from-file");
        var resolver = new CredentialResolver(new FakeEnvironmentReader(), store);

        // An SSH remote authenticates with keys; git handles it and dotquay must not treat the
        // absence of a stored password as a failure.
        var lookup = resolver.ForGitRepository("git@gitlab.example.com:kani/addon.git");

        Assert.Null(lookup.Credential);
        Assert.Equal(CredentialOrigin.None, lookup.Origin);
    }
}
