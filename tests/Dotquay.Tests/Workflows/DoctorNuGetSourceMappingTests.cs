using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <c>dotquay doctor</c>'s check for a package the lockfile binds to a private NuGet feed
/// that <c>NuGet.Config</c> does not bind there.
/// </summary>
/// <remarks>
/// The check exists because of a failure that reported success everywhere: an <c>add</c> rewrote the
/// mapping from the one package it was installing and dropped VBeami.Core's binding, and the machine
/// it happened on restored fine anyway because a local link stood in for the feed. Nothing was wrong
/// with the lockfile, the manifest, or addons/ - only with a generated file nothing was looking at.
/// </remarks>
public class DoctorNuGetSourceMappingTests {
    private const string ProjectDirectory = "project";
    private const string FeedUrl = "https://gitlab.example.com/api/v4/projects/130/packages/nuget/index.json";

    private const string ManifestWithPrivateFeed = $"""
        [project]

        [nuget.sources.lazy_o]
        url = "{FeedUrl}"

        [dotnet."VBeami.Core"]
        version = "0.7.21"
        """;

    private const string LockedToPrivateFeed = """
        [[dotnet]]
        name = "VBeami.Core"
        resolved = "0.7.21"
        source = "lazy_o"
        """;

    private static string Config(string mapping) => $"""
        <configuration>
          <packageSources>
            <add key="dotquay-lazy_o" value="{FeedUrl}" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
        {mapping}
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """;

    private static async Task<IReadOnlyList<DoctorIssue>> RunAsync(
        string manifest = ManifestWithPrivateFeed, string lockfile = LockedToPrivateFeed, string? config = null) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), manifest);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), lockfile);

        if (config is not null) {
            fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "NuGet.Config"), config);
        }

        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache");
        var result = await workflow.DoctorAsync(ProjectDirectory);

        return result.Issues.Where(issue => issue.Kind == DoctorIssueKind.NuGetSourceMappingDrift).ToList();
    }

    [Fact]
    public async Task DoctorAsync_AMappingThatDoesNotNameThePackage_IsReported() {
        var issues = await RunAsync(config: Config("""
                <packageSource key="dotquay-lazy_o">
                  <package pattern="Something.Else" />
                </packageSource>
            """));

        // The shape the add bug left behind: the feed is still declared, so nothing looks missing,
        // but the package it was declared for is no longer bound to it.
        var issue = Assert.Single(issues);
        Assert.Contains("VBeami.Core", issue.Message);
        Assert.Contains("dotquay-lazy_o", issue.Message);
        Assert.Contains("dotquay install", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_NoNuGetConfigAtAll_IsReported() {
        // Worse than a partial mapping, not better: restore does not know the feed exists either.
        Assert.Single(await RunAsync());
    }

    [Fact]
    public async Task DoctorAsync_AMappingThatBindsThePackage_IsNotReported() {
        var issues = await RunAsync(config: Config("""
                <packageSource key="dotquay-lazy_o">
                  <package pattern="VBeami.Core" />
                </packageSource>
            """));

        Assert.Empty(issues);
    }

    [Fact]
    public async Task DoctorAsync_APrefixPatternCoveringThePackage_IsNotReported() {
        var issues = await RunAsync(config: Config("""
                <packageSource key="dotquay-lazy_o">
                  <package pattern="VBeami.*" />
                </packageSource>
            """));

        // An owned pattern from [nuget.sources.lazy_o] packages binds the whole family, which is how
        // a private package's own private transitive dependencies stay off nuget.org.
        Assert.Empty(issues);
    }

    [Fact]
    public async Task DoctorAsync_APackageResolvedFromNuGetOrg_IsNotReported() {
        var issues = await RunAsync(lockfile: """
            [[dotnet]]
            name = "Newtonsoft.Json"
            resolved = "13.0.3"
            """);

        // No source recorded means nuget.org, which the * catch-all already covers. Reporting it
        // would fire on nearly every package in every project.
        Assert.Empty(issues);
    }

    [Fact]
    public async Task DoctorAsync_SourceMappingTurnedOff_IsNotReported() {
        var issues = await RunAsync(manifest: $"""
            [project]

            [nuget]
            source_mapping = false

            [nuget.sources.lazy_o]
            url = "{FeedUrl}"
            """);

        // dotquay writes no mapping at all here, so there is nothing to have drifted from - and a
        // project that opted out must not be told to run install forever.
        Assert.Empty(issues);
    }

    [Fact]
    public async Task DoctorAsync_AManifestPin_DecidesTheExpectedFeed() {
        var issues = await RunAsync(
            manifest: $"""
                [project]

                [nuget.sources.lazy_o]
                url = "{FeedUrl}"

                [nuget.sources.other]
                url = "https://gitlab.example.com/api/v4/projects/999/packages/nuget/index.json"

                [dotnet."VBeami.Core"]
                version = "0.7.21"
                source = "other"
                """,
            config: Config("""
                    <packageSource key="dotquay-lazy_o">
                      <package pattern="VBeami.Core" />
                    </packageSource>
                """));

        // The pin is what the next write will honour, so a file still bound to the feed that answered
        // last time is drift, not health.
        var issue = Assert.Single(issues);
        Assert.Contains("dotquay-other", issue.Message);
    }
}
