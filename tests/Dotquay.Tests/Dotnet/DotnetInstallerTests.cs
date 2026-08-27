using Dotquay.Core.Dotnet;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet;

public class DotnetInstallerTests
{
    private const string SdkStyleCsproj = """
        <Project Sdk="Godot.NET.Sdk/4.3.0">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string UsernameVar = "DOTQUAY_NUGET_USERNAME";
    private const string TokenVar = "DOTQUAY_NUGET_TOKEN";
    private const string PrivateSourceUrl = "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json";

    private static (DotnetInstaller Installer, InMemoryFileSystem FileSystem, FakeDotnetCliRunner Runner) Build()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);
        var runner = new FakeDotnetCliRunner();
        return (new DotnetInstaller(fileSystem, runner), fileSystem, runner);
    }

    private static async Task WithCredentialsAsync(string? username, string? token, Func<Task> action)
    {
        var previousUsername = Environment.GetEnvironmentVariable(UsernameVar);
        var previousToken = Environment.GetEnvironmentVariable(TokenVar);
        try
        {
            Environment.SetEnvironmentVariable(UsernameVar, username);
            Environment.SetEnvironmentVariable(TokenVar, token);
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(UsernameVar, previousUsername);
            Environment.SetEnvironmentVariable(TokenVar, previousToken);
        }
    }

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

        await installer.InstallAsync("project", "Acme.Internal", "1.0.0", PrivateSourceUrl);

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.Contains(PrivateSourceUrl, config);
        Assert.Contains(NuGetConfigWriter.DotquaySourceName, config);
    }

    [Fact]
    public async Task InstallAsync_WithNuGetSourceAndCredentials_PassesTranslatedEnvVarToRestore()
    {
        await WithCredentialsAsync("kani", "glpat-secret", async () =>
        {
            var (installer, _, runner) = Build();

            await installer.InstallAsync("project", "Acme.Internal", "1.0.0", PrivateSourceUrl);

            var env = runner.RestoreCalls.Single().EnvironmentVariables;
            Assert.NotNull(env);
            Assert.Equal("Username=kani;Password=glpat-secret", env![$"NuGetPackageSourceCredentials_{NuGetConfigWriter.DotquaySourceName}"]);
        });
    }

    [Fact]
    public async Task InstallAsync_WithNuGetSourceButNoCredentialsConfigured_RestoresWithoutCredentialEnv()
    {
        await WithCredentialsAsync(null, null, async () =>
        {
            var (installer, _, runner) = Build();

            await installer.InstallAsync("project", "Acme.Internal", "1.0.0", PrivateSourceUrl);

            Assert.Null(runner.RestoreCalls.Single().EnvironmentVariables);
        });
    }

    [Fact]
    public async Task InstallAsync_NuGetConfigNeverContainsRawSecret()
    {
        await WithCredentialsAsync("kani", "glpat-secret", async () =>
        {
            var (installer, fileSystem, _) = Build();

            await installer.InstallAsync("project", "Acme.Internal", "1.0.0", PrivateSourceUrl);

            // credentials only ever flow through the restore process's environment, never to disk
            Assert.DoesNotContain("glpat-secret", fileSystem.ReadAllText("project/NuGet.Config"));
        });
    }
}
