using Dotquay.Core.Auth;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Auth;

/// <summary>
/// The invariant everything else in the auth design rests on: a configured token reaches the
/// <c>dotnet restore</c> child process's environment and <em>nothing else</em> — not
/// <c>dotquay.toml</c>, not <c>dotquay.lock</c>, not <c>NuGet.Config</c>, not any file dotquay writes
/// into a project.
/// </summary>
/// <remarks>
/// Asserted rather than reviewed. Every one of those files is committed to git, and the failure mode
/// is a token in a repository's history — which is not something a code-review habit should be the
/// only thing standing between the user and.
/// </remarks>
public class SecretLeakTests {
    private const string Token = "glpat-SUPERSECRET-do-not-commit";
    private const string SourceUrl = "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json";

    private const string Csproj = """
        <Project Sdk="Godot.NET.Sdk/4.3.0">
          <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
        </Project>
        """;

    [Fact]
    public async Task InstallingWithCredentials_LeavesTheTokenInNoFileOnDisk() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", Csproj);

        var sources = NuGetSourceSet.From([new NuGetSourceSpec("core", SourceUrl)]);
        var runner = new FakeDotnetCliRunner();
        var installer = new DotnetInstaller(
            fileSystem, runner, TestCredentials.ForHost("gitlab.example.com", "kani", Token));

        await installer.InstallAsync("project", "Acme.Internal", "1.0.0", sources, resolvedSourceName: "core");

        // Everything dotquay wrote, whatever it wrote.
        foreach (var path in fileSystem.AllFilePaths()) {
            Assert.DoesNotContain(Token, fileSystem.ReadAllText(path), StringComparison.Ordinal);
        }

        // ...and it did reach the one place it is supposed to.
        Assert.Contains(Token, runner.RestoreCalls.Single().EnvironmentVariables!.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void SerializedManifestAndLockfile_NeverCarryACredentialField() {
        var manifest = new ProjectManifest {
            NuGetSources = [new NuGetSourceSpec("core", SourceUrl)],
            Dotnet = new Dictionary<string, DotnetSpec> {
                ["Acme.Internal"] = new DotnetSpec { Name = "Acme.Internal", Version = "1.0.0", Source = "core" },
            },
        };

        var lockfile = new DotquayLockfile {
            Dotnet = [new LockedDotnet { Name = "Acme.Internal", Resolved = "1.0.0", Source = "core" }],
        };

        var manifestToml = ManifestWriter.Serialize(manifest);
        var lockfileToml = LockfileWriter.Serialize(lockfile);

        foreach (var text in new[] { manifestToml, lockfileToml }) {
            Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("username", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ListedCredentials_ExposeNoPartOfTheToken() {
        var store = new InMemoryCredentialStore().Add("gitlab.example.com", "kani", Token);

        var listed = Assert.Single(store.List());

        // Not even a truncated prefix: "glpat-" plus a host is useful to an attacker and useless to
        // the person reading it.
        Assert.DoesNotContain(Token[..8], listed.Scope + listed.Username, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialExceptions_NeverEmbedTheToken() {
        var fileSystem = new InMemoryFileSystem();
        var store = new TomlCredentialStore(fileSystem, "/home/kani/.dotquay/credentials.toml");
        store.Set(CredentialScope.Parse("gitlab.example.com"), new Credential("kani", Token));

        // Corrupt the file so reading it throws, and check the diagnostic doesn't quote the secret
        // back — error messages travel into bug reports and terminal scrollback.
        fileSystem.WriteAllText("/home/kani/.dotquay/credentials.toml", $"[credential.\"gitlab.example.com\"]\ntoken = \"{Token}\"\n");

        var exception = Assert.Throws<CredentialsFileException>(() => store.List());

        Assert.DoesNotContain(Token, exception.Message, StringComparison.Ordinal);
    }
}
