using Dotquay.Core.Auth;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Manifest;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet;

/// <summary>
/// Covers <see cref="DotnetInstaller"/>'s two jobs: writing the <c>PackageReference</c>, and making
/// the separate <c>dotnet restore</c> process able to reach whatever private feeds the project
/// declares — without any credential ever reaching disk.
/// </summary>
/// <remarks>
/// Credentials arrive through <see cref="FakeEnvironmentReader"/> and
/// <see cref="InMemoryCredentialStore"/> rather than real process environment variables. The previous
/// save-set-restore approach was safe within this class and raced <c>NuGetV3ClientFactoryTests</c>,
/// which mutated the same two variables in a class xunit runs in parallel with this one.
/// </remarks>
public class DotnetInstallerTests
{
    private const string SdkStyleCsproj = """
        <Project Sdk="Godot.NET.Sdk/4.3.0">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string PrivateSourceUrl = "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json";
    private const string SecondSourceUrl = "https://gitlab.example.com/api/v4/projects/2/packages/nuget/index.json";

    private static readonly NuGetSourceSet PrivateSources =
        NuGetSourceSet.From([new NuGetSourceSpec(NuGetSourceSpec.LegacyName, PrivateSourceUrl)]);

    private static (DotnetInstaller Installer, InMemoryFileSystem FileSystem, FakeDotnetCliRunner Runner) Build(
        string? username = null, string? token = null, ICredentialStoreSeed? seed = null)
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);

        var environment = new FakeEnvironmentReader();
        if (username is not null && token is not null)
        {
            environment
                .Set(CredentialResolver.GlobalUsernameVariable, username)
                .Set(CredentialResolver.GlobalTokenVariable, token);
        }

        var store = new InMemoryCredentialStore();
        seed?.Invoke(store);

        var runner = new FakeDotnetCliRunner();
        return (new DotnetInstaller(fileSystem, runner, new CredentialResolver(environment, store)), fileSystem, runner);
    }

    public delegate void ICredentialStoreSeed(InMemoryCredentialStore store);

    [Fact]
    public async Task InstallAsync_WritesReferenceAndRestoresByDefault()
    {
        var (installer, fileSystem, runner) = Build();

        var locked = await installer.InstallAsync("project", "Newtonsoft.Json", "13.0.3");

        Assert.Equal("Newtonsoft.Json", locked.Name);
        Assert.Equal("13.0.3", locked.Resolved);
        Assert.Contains("Newtonsoft.Json", fileSystem.ReadAllText("project/Game.csproj"));
        Assert.Equal(["project/Game.csproj"], runner.RestoredCsprojPaths);
    }

    [Fact]
    public async Task InstallAsync_RecordsTheResolvedSourceOnTheLockEntry()
    {
        var (installer, _, _) = Build();

        var locked = await installer.InstallAsync(
            "project", "Acme.Internal", "1.0.0", PrivateSources, resolvedSourceName: "default");

        Assert.Equal("default", locked.Source);
    }

    [Fact]
    public async Task InstallAsync_PublicPackage_LeavesSourceNull()
    {
        var (installer, _, _) = Build();

        var locked = await installer.InstallAsync("project", "Newtonsoft.Json", "13.0.3");

        // Null keeps a public-only project's lockfile byte-identical to what pre-v0.7 wrote.
        Assert.Null(locked.Source);
    }

    [Fact]
    public async Task InstallAsync_RestoreFalse_WritesReferenceButSkipsRestore()
    {
        var (installer, fileSystem, runner) = Build();

        await installer.InstallAsync("project", "Newtonsoft.Json", "13.0.3", restore: false);

        Assert.Contains("Newtonsoft.Json", fileSystem.ReadAllText("project/Game.csproj"));
        Assert.Empty(runner.RestoreCalls);
    }

    [Fact]
    public async Task InstallAsync_RestoreFailure_Throws()
    {
        var (installer, _, runner) = Build();
        runner.ThrowOnRestore = true;

        await Assert.ThrowsAsync<DotnetSyncException>(() => installer.InstallAsync("project", "Newtonsoft.Json", "13.0.3"));
    }

    [Fact]
    public void Remove_ExistingReference_RemovesItAndReturnsTrue()
    {
        var (installer, fileSystem, _) = Build();
        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3");

        var removed = installer.Remove("project", "Newtonsoft.Json");

        Assert.True(removed);
        Assert.DoesNotContain("Newtonsoft.Json", fileSystem.ReadAllText("project/Game.csproj"));
    }

    [Fact]
    public async Task RestoreAsync_LocatesCsprojAndRestores()
    {
        var (installer, _, runner) = Build();

        await installer.RestoreAsync("project");

        Assert.Equal(["project/Game.csproj"], runner.RestoredCsprojPaths);
    }

    [Fact]
    public async Task InstallAsync_NoNuGetSource_LeavesNuGetConfigUntouchedAndSendsNoCredentialEnv()
    {
        var (installer, fileSystem, runner) = Build();

        await installer.InstallAsync("project", "Newtonsoft.Json", "13.0.3");

        Assert.False(fileSystem.FileExists("project/NuGet.Config"));
        Assert.Null(runner.RestoreCalls.Single().EnvironmentVariables);
    }

    [Fact]
    public async Task InstallAsync_WithNuGetSource_WritesNuGetConfigDeclaringIt()
    {
        var (installer, fileSystem, _) = Build();

        await installer.InstallAsync("project", "Acme.Internal", "1.0.0", PrivateSources);

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.Contains(PrivateSourceUrl, config);
        Assert.Contains(NuGetConfigWriter.DotquaySourceName, config);
    }

    [Fact]
    public async Task InstallAsync_WithNuGetSourceAndCredentials_PassesTranslatedEnvVarToRestore()
    {
        var (installer, _, runner) = Build("kani", "glpat-secret");

        await installer.InstallAsync("project", "Acme.Internal", "1.0.0", PrivateSources);

        var env = runner.RestoreCalls.Single().EnvironmentVariables;
        Assert.NotNull(env);
        Assert.Equal(
            "Username=kani;Password=glpat-secret",
            env![$"NuGetPackageSourceCredentials_{NuGetConfigWriter.DotquaySourceName}"]);
    }

    [Fact]
    public async Task InstallAsync_TwoAuthenticatedSources_PassesOneEnvVarEach()
    {
        var (installer, _, runner) = Build("kani", "glpat-secret");
        var sources = NuGetSourceSet.From(
        [
            new NuGetSourceSpec("core", PrivateSourceUrl),
            new NuGetSourceSpec("tools", SecondSourceUrl),
        ]);

        await installer.InstallAsync("project", "Acme.Internal", "1.0.0", sources);

        var env = runner.RestoreCalls.Single().EnvironmentVariables!;
        Assert.Equal("Username=kani;Password=glpat-secret", env["NuGetPackageSourceCredentials_dotquay-core"]);
        Assert.Equal("Username=kani;Password=glpat-secret", env["NuGetPackageSourceCredentials_dotquay-tools"]);

        // nuget.org is appended to every source set and must never be handed a credential.
        Assert.DoesNotContain(env.Keys, key => key.Contains("nuget.org", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_CredentialFromTheStore_ReachesRestoreJustLikeAnEnvironmentOne()
    {
        var (installer, _, runner) = Build(
            seed: store => store.Add("gitlab.example.com", "kani", "from-login"));

        await installer.InstallAsync("project", "Acme.Internal", "1.0.0", PrivateSources);

        var env = runner.RestoreCalls.Single().EnvironmentVariables!;
        Assert.Equal(
            "Username=kani;Password=from-login",
            env[$"NuGetPackageSourceCredentials_{NuGetConfigWriter.DotquaySourceName}"]);
    }

    [Fact]
    public async Task InstallAsync_WithNuGetSourceButNoCredentialsConfigured_RestoresWithoutCredentialEnv()
    {
        var (installer, _, runner) = Build();

        await installer.InstallAsync("project", "Acme.Internal", "1.0.0", PrivateSources);

        Assert.Null(runner.RestoreCalls.Single().EnvironmentVariables);
    }

    [Fact]
    public async Task InstallAsync_NuGetConfigNeverContainsRawSecret()
    {
        var (installer, fileSystem, _) = Build("kani", "glpat-secret");

        await installer.InstallAsync("project", "Acme.Internal", "1.0.0", PrivateSources);

        // Credentials only ever flow through the restore process's environment, never to disk.
        Assert.DoesNotContain("glpat-secret", fileSystem.ReadAllText("project/NuGet.Config"));
    }
}
