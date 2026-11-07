using Dotquay.Core.Dotnet;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet;

/// <summary>
/// Covers the <c>&lt;packageSourceMapping&gt;</c> a single <c>add</c> writes.
/// </summary>
/// <remarks>
/// <para>These exist because of a failure no unit test could have caught before: the mapping was
/// built from the manifest's pins alone, read from disk — and during <c>add</c> the package being
/// added is not in the manifest yet, since the manifest is written only after the install succeeds.
/// So the file held nothing but the <c>*</c> catch-all aimed at nuget.org, and <c>dotnet
/// restore</c> rejected a package that had just resolved cleanly from a private GitLab feed with
/// <c>NU1101 ... the following source(s) were not considered: dotquay-lazy_o</c>.</para>
/// <para>It only showed up when the published binary was run against the real feed, because
/// <see cref="FakeDotnetCliRunner"/> does not read <c>NuGet.Config</c> — nothing in the suite was
/// looking at the file. These tests look at the file.</para>
/// </remarks>
public class AddSourceMappingTests {
    private const string ProjectDir = "project";
    private const string PrivateUrl =
        "https://gitlab.example.com/api/v4/projects/130/packages/nuget/index.json";

    private const string Csproj = """
        <Project Sdk="Godot.NET.Sdk/4.7.2">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private static (DependencyWorkflow Workflow, InMemoryFileSystem FileSystem) Build(
        string manifest, string? feedName, string? lockfile = null) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", manifest);
        fileSystem.WriteAllText($"{ProjectDir}/Game.csproj", Csproj);

        if (lockfile is not null) {
            fileSystem.WriteAllText($"{ProjectDir}/dotquay.lock", lockfile);
        }

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };

        // Named so the resolved-source name the client reports is the declared feed's, exactly as
        // the real MultiSourceNuGetClient reports it.
        var nuGetClient = new FakeNuGetClient(feedName);
        nuGetClient.VersionsByPackageId["Kani.Core"] = ["1.2.0", "1.3.0"];

        var workflow = new DependencyWorkflow(
            fileSystem,
            gitClient,
            new FakeNuGetClientFactory(nuGetClient),
            new AddonInstaller(
                fileSystem, new RepoCache(fileSystem, gitClient, "cache"),
                new SyncEngine(fileSystem), new FakePatchTool(fileSystem)),
            new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None()));

        return (workflow, fileSystem);
    }

    private static string ReadNuGetConfig(InMemoryFileSystem fileSystem) =>
        fileSystem.ReadAllText($"{ProjectDir}/NuGet.Config");

    private const string ManifestWithPrivateFeed = $"""
        [nuget.sources.lazy_o]
        url = "{PrivateUrl}"
        """;

    /// <summary>The regression. Before the fix this produced a config whose only pattern was
    /// <c>*</c> -> nuget.org, and a real restore refused the package.</summary>
    [Fact]
    public async Task AnAddBindsRestoreToTheFeedThePackageResolvedFrom() {
        var (workflow, fileSystem) = Build(ManifestWithPrivateFeed, feedName: "lazy_o");

        await workflow.AddDotnetAsync(ProjectDir, "Kani.Core");

        var config = ReadNuGetConfig(fileSystem);
        Assert.Contains("<packageSource key=\"dotquay-lazy_o\">", config);
        Assert.Contains("<package pattern=\"Kani.Core\" />", config);
    }

    [Fact]
    public async Task AnExplicitSourcePinIsMappedToo() {
        var (workflow, fileSystem) = Build(ManifestWithPrivateFeed, feedName: "lazy_o");

        await workflow.AddDotnetAsync(ProjectDir, "Kani.Core", versionOverride: "1.2.0",
            pinnedSourceName: "lazy_o");

        var config = ReadNuGetConfig(fileSystem);
        Assert.Contains("<packageSource key=\"dotquay-lazy_o\">", config);
        Assert.Contains("<package pattern=\"Kani.Core\" />", config);
    }

    /// <summary>An exact version is the case that first failed in the wild, so it gets its own
    /// test: resolution still has to report which feed answered, or the mapping has nothing to
    /// write.</summary>
    [Fact]
    public async Task AnExactVersionStillBindsTheFeed() {
        var (workflow, fileSystem) = Build(ManifestWithPrivateFeed, feedName: "lazy_o");

        await workflow.AddDotnetAsync(ProjectDir, "Kani.Core", versionOverride: "1.2.0");

        Assert.Contains("<package pattern=\"Kani.Core\" />", ReadNuGetConfig(fileSystem));
    }

    /// <summary>A package from the public feed needs no mapping of its own — the <c>*</c> catch-all
    /// already points there, and an entry per public package would be noise in a committed
    /// file.</summary>
    [Fact]
    public async Task APublicPackageIsLeftToTheCatchAll() {
        var (workflow, fileSystem) = Build(ManifestWithPrivateFeed, feedName: null);

        await workflow.AddDotnetAsync(ProjectDir, "Kani.Core");

        var config = ReadNuGetConfig(fileSystem);
        Assert.DoesNotContain("<package pattern=\"Kani.Core\" />", config);
        Assert.Contains("<package pattern=\"*\" />", config);
    }

    private const string LockfileWithPrivatePackage = """
        [[dotnet]]
        name = "VBeami.Core"
        resolved = "0.7.21"
        source = "lazy_o"
        """;

    /// <summary>
    /// The second regression, reported 2026-09-15: adding one package un-mapped every other one.
    /// </summary>
    /// <remarks>
    /// The mapping element is rewritten wholesale on every write, so an install that knows only the
    /// package it is installing does not leave the others alone, it deletes them. VBeami.Core then
    /// matched nothing but the <c>*</c> catch-all, and would have been fetched from nuget.org on
    /// any machine without a local link to hide it.
    /// </remarks>
    [Fact]
    public async Task AnAddKeepsTheMappingOfEveryPackageAlreadyLocked() {
        var (workflow, fileSystem) = Build(
            ManifestWithPrivateFeed, feedName: "lazy_o", lockfile: LockfileWithPrivatePackage);

        await workflow.AddDotnetAsync(ProjectDir, "Kani.Core");

        var config = ReadNuGetConfig(fileSystem);
        Assert.Contains("<package pattern=\"Kani.Core\" />", config);
        Assert.Contains("<package pattern=\"VBeami.Core\" />", config);
    }

    /// <summary>The same for a package left on nuget.org: it needs no pattern of its own, and the
    /// catch-all it relies on has to survive the add.</summary>
    [Fact]
    public async Task AnAddLeavesAnAlreadyLockedPublicPackageOnTheCatchAll() {
        var (workflow, fileSystem) = Build(
            ManifestWithPrivateFeed,
            feedName: "lazy_o",
            lockfile: """
                [[dotnet]]
                name = "Newtonsoft.Json"
                resolved = "13.0.3"
                """);

        await workflow.AddDotnetAsync(ProjectDir, "Kani.Core");

        var config = ReadNuGetConfig(fileSystem);
        Assert.DoesNotContain("<package pattern=\"Newtonsoft.Json\" />", config);
        Assert.Contains("<package pattern=\"*\" />", config);
    }
}
