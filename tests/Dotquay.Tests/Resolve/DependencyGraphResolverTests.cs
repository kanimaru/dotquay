using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Resolve;

public class DependencyGraphResolverTests {
    private const string LeafUrl = "https://example.com/leaf.git";
    private const string CoreUrl = "https://example.com/core.git";
    private const string AUrl = "https://example.com/a.git";
    private const string BUrl = "https://example.com/b.git";

    private static (DependencyGraphResolver Resolver, InMemoryFileSystem FileSystem,
        FakeGitClient GitClient) Build() {
        var fileSystem = new InMemoryFileSystem();
        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        var cache = new RepoCache(fileSystem, gitClient, "cache");
        var resolver = new DependencyGraphResolver(fileSystem, gitClient, cache, "discovery");
        return (resolver, fileSystem, gitClient);
    }

    private static void SetTags(FakeGitClient gitClient, string repoUrl, params string[] tags) =>
        gitClient.RemoteRefsByUrl[repoUrl] = tags
            .Select((tag, i) => new GitRef(tag, new string('a', 39) + i, GitRefKind.Tag))
            .ToList();

    private static Dictionary<string, AddonSpec> NoOverrides() => new();

    [Fact]
    public async Task ResolveGraphAsync_LeafAddon_HasNoDependencies() {
        var (resolver, _, gitClient) = Build();
        SetTags(gitClient, LeafUrl, "v1.0.0");
        gitClient.FetchedFilesByUrl[LeafUrl] = new Dictionary<string, string> {
            ["addons/leaf/plugin.cfg"] = "cfg",
        };

        var rootAddons = new Dictionary<string, AddonSpec> {
            ["leaf"] = new AddonSpec {
                Name = "leaf",
                Git = LeafUrl,
                Version = "~1.0",
                Path = "addons/leaf"
            },
        };

        var result = await resolver.ResolveGraphAsync(rootAddons, NoOverrides());

        var entry = Assert.Single(result.Addons);
        Assert.Equal("leaf", entry.Resolved.Name);
        Assert.Empty(entry.DependencyNames);
        Assert.Empty(result.AddonDeclaredDotnet);
    }

    [Fact]
    public async Task ResolveGraphAsync_LeafAddonWithNonPackageDotquayToml_TreatedAsLeaf() {
        var (resolver, _, gitClient) = Build();
        SetTags(gitClient, LeafUrl, "v1.0.0");
        gitClient.FetchedFilesByUrl[LeafUrl] = new Dictionary<string, string> {
            ["addons/leaf/plugin.cfg"] = "cfg",
            // A project-style manifest, not a [package] one -- some addons might ship an unrelated
            // dotquay.toml by coincidence; it must not crash the graph walk (SPEC.md §7.2).
            ["addons/leaf/dotquay.toml"] = "[project]\ngodot = \"4.3\"\n",
        };

        var rootAddons = new Dictionary<string, AddonSpec> {
            ["leaf"] = new AddonSpec {
                Name = "leaf",
                Git = LeafUrl,
                Version = "~1.0",
                Path = "addons/leaf"
            },
        };

        var result = await resolver.ResolveGraphAsync(rootAddons, NoOverrides());

        var entry = Assert.Single(result.Addons);
        Assert.Empty(entry.DependencyNames);
    }

    private static void SetUpDiamond(FakeGitClient gitClient, string aCoreConstraint,
        string bCoreConstraint) {
        SetTags(gitClient, CoreUrl, "v1.2.0", "v1.3.0", "v1.4.0", "v1.4.2", "v2.0.0");
        gitClient.FetchedFilesByUrl[CoreUrl] = new Dictionary<string,
            string> { ["addons/core/plugin.cfg"] = "core" };

        gitClient.RemoteRefsByUrl[AUrl] = [];
        gitClient.FetchedFilesByUrl[AUrl] = new Dictionary<string, string> {
            ["addons/a/plugin.cfg"] = "a",
            ["addons/a/dotquay.toml"] = $"""
                [package]
                name = "a"
                version = "1.0.0"

                [addons.core]
                git = "{CoreUrl}"
                version = "{aCoreConstraint}"
                """,
        };

        gitClient.RemoteRefsByUrl[BUrl] = [];
        gitClient.FetchedFilesByUrl[BUrl] = new Dictionary<string, string> {
            ["addons/b/plugin.cfg"] = "b",
            ["addons/b/dotquay.toml"] = $"""
                [package]
                name = "b"
                version = "1.0.0"

                [addons.core]
                git = "{CoreUrl}"
                version = "{bCoreConstraint}"
                """,
        };
    }

    private static Dictionary<string, AddonSpec> DiamondRoots() => new() {
        ["a"] = new AddonSpec { Name = "a", Git = AUrl, Rev = "reva", Path = "addons/a" },
        ["b"] = new AddonSpec { Name = "b", Git = BUrl, Rev = "revb", Path = "addons/b" },
    };

    [Fact]
    public async Task ResolveGraphAsync_Diamond_UnifiesToHighestVersionSatisfyingBothConstraints() {
        var (resolver, _, gitClient) = Build();
        // "~1" matches any 1.x.x; "~1.4" matches only 1.4.x -- the overlap is 1.4.x, so the highest
        // tag satisfying both is v1.4.2 (SPEC.md §8's diamond example, expressed in this codebase's
        // actual tilde-range grammar rather than the illustrative ">=" shorthand in the spec
        // prose).
        SetUpDiamond(gitClient, aCoreConstraint: "~1", bCoreConstraint: "~1.4");

        var result = await resolver.ResolveGraphAsync(DiamondRoots(), NoOverrides());

        Assert.Equal(3, result.Addons.Count);
        var core = Assert.Single(result.Addons, e => e.Resolved.Name == "core");
        Assert.Equal("v1.4.2", core.Resolved.DisplayVersion);

        var a = Assert.Single(result.Addons, e => e.Resolved.Name == "a");
        Assert.Equal(["core"], a.DependencyNames);
    }

    [Fact]
    public async Task ResolveGraphAsync_DisjointVersionRanges_ThrowsNamingBothRequirers() {
        var (resolver, _, gitClient) = Build();
        // "~1.2" (< 1.3) and "~1.4" (>= 1.4, < 1.5) never overlap -- no tag can satisfy both.
        SetUpDiamond(gitClient, aCoreConstraint: "~1.2", bCoreConstraint: "~1.4");

        var ex = await Assert.ThrowsAsync<AddonResolutionException>(
            () => resolver.ResolveGraphAsync(DiamondRoots(), NoOverrides()));

        Assert.Contains("'a'", ex.Message);
        Assert.Contains("'b'", ex.Message);
        Assert.Contains("core", ex.Message);
    }

    [Fact]
    public async Task ResolveGraphAsync_ConflictingRevPins_ThrowsNamingBothRequirers() {
        var (resolver, _, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.0.0");
        gitClient.FetchedFilesByUrl[CoreUrl] = new Dictionary<string,
            string> { ["addons/core/plugin.cfg"] = "core" };

        gitClient.RemoteRefsByUrl[AUrl] = [];
        gitClient.FetchedFilesByUrl[AUrl] = new Dictionary<string, string> {
            ["addons/a/plugin.cfg"] = "a",
            ["addons/a/dotquay.toml"] = $"""
                [package]
                name = "a"
                version = "1.0.0"

                [addons.core]
                git = "{CoreUrl}"
                rev = "{new string('a', 40)}"
                """,
        };

        gitClient.RemoteRefsByUrl[BUrl] = [];
        gitClient.FetchedFilesByUrl[BUrl] = new Dictionary<string, string> {
            ["addons/b/plugin.cfg"] = "b",
            ["addons/b/dotquay.toml"] = $"""
                [package]
                name = "b"
                version = "1.0.0"

                [addons.core]
                git = "{CoreUrl}"
                rev = "{new string('b', 40)}"
                """,
        };

        var ex = await Assert.ThrowsAsync<AddonResolutionException>(
            () => resolver.ResolveGraphAsync(DiamondRoots(), NoOverrides()));

        Assert.Contains("'a'", ex.Message);
        Assert.Contains("'b'", ex.Message);
    }

    [Fact]
    public async Task ResolveGraphAsync_SameNameDifferentGitUrls_Throws() {
        var (resolver, _, gitClient) = Build();

        gitClient.RemoteRefsByUrl[AUrl] = [];
        gitClient.FetchedFilesByUrl[AUrl] = new Dictionary<string, string> {
            ["addons/a/plugin.cfg"] = "a",
            ["addons/a/dotquay.toml"] = """
                [package]
                name = "a"
                version = "1.0.0"

                [addons.core]
                git = "https://example.com/core-fork-1.git"
                rev = "1111111111111111111111111111111111aaaa"
                """,
        };

        gitClient.RemoteRefsByUrl[BUrl] = [];
        gitClient.FetchedFilesByUrl[BUrl] = new Dictionary<string, string> {
            ["addons/b/plugin.cfg"] = "b",
            ["addons/b/dotquay.toml"] = """
                [package]
                name = "b"
                version = "1.0.0"

                [addons.core]
                git = "https://example.com/core-fork-2.git"
                rev = "2222222222222222222222222222222222bbbb"
                """,
        };

        var ex = await Assert.ThrowsAsync<AddonResolutionException>(
            () => resolver.ResolveGraphAsync(DiamondRoots(), NoOverrides()));

        Assert.Contains("different git repositories", ex.Message);
    }

    [Fact]
    public async Task ResolveGraphAsync_OverrideOnConflictingName_WinsUnconditionally() {
        var (resolver, _, gitClient) = Build();
        // would normally conflict
        SetUpDiamond(gitClient, aCoreConstraint: "~1.2", bCoreConstraint: "~1.4");

        const string overrideRev = "3333333333333333333333333333333333cccc";
        gitClient.FetchedFilesByUrlAndSha[(CoreUrl, overrideRev)] = new Dictionary<string, string> {
            ["addons/core/plugin.cfg"] = "overridden core",
        };

        var overrides = new Dictionary<string, AddonSpec> {
            ["core"] = new AddonSpec {
                Name = "core",
                Git = CoreUrl,
                Rev = overrideRev,
                Path = "addons/core"
            },
        };

        var result = await resolver.ResolveGraphAsync(DiamondRoots(), overrides);

        var core = Assert.Single(result.Addons, e => e.Resolved.Name == "core");
        Assert.Equal(overrideRev, core.Resolved.ResolvedSha);
    }

    [Fact]
    public async Task ResolveGraphAsync_AddonDeclaredDotnetDependency_IsUnioned() {
        var (resolver, _, gitClient) = Build();
        SetTags(gitClient, LeafUrl, "v1.0.0");
        gitClient.FetchedFilesByUrl[LeafUrl] = new Dictionary<string, string> {
            ["addons/leaf/plugin.cfg"] = "cfg",
            ["addons/leaf/dotquay.toml"] = """
                [package]
                name = "leaf"
                version = "1.0.0"

                [dotnet.Newtonsoft.Json]
                version = "13.0.*"
                """,
        };

        var rootAddons = new Dictionary<string, AddonSpec> {
            ["leaf"] = new AddonSpec {
                Name = "leaf",
                Git = LeafUrl,
                Version = "~1.0",
                Path = "addons/leaf"
            },
        };

        var result = await resolver.ResolveGraphAsync(rootAddons, NoOverrides());

        var dotnet = Assert.Single(result.AddonDeclaredDotnet);
        Assert.Equal("Newtonsoft.Json", dotnet.Name);
        Assert.Equal("13.0.*", dotnet.Version);
    }

    [Fact]
    public async Task ResolveGraphAsync_Cycle_DoesNotInfiniteLoop() {
        var (resolver, _, gitClient) = Build();

        gitClient.RemoteRefsByUrl[AUrl] = [];
        gitClient.FetchedFilesByUrl[AUrl] = new Dictionary<string, string> {
            ["addons/a/plugin.cfg"] = "a",
            ["addons/a/dotquay.toml"] = $"""
                [package]
                name = "a"
                version = "1.0.0"

                [addons.b]
                git = "{BUrl}"
                rev = "{new string('b', 40)}"
                """,
        };

        gitClient.RemoteRefsByUrl[BUrl] = [];
        gitClient.FetchedFilesByUrl[BUrl] = new Dictionary<string, string> {
            ["addons/b/plugin.cfg"] = "b",
            ["addons/b/dotquay.toml"] = $"""
                [package]
                name = "b"
                version = "1.0.0"

                [addons.a]
                git = "{AUrl}"
                rev = "{new string('a', 40)}"
                """,
        };

        var rootAddons = new Dictionary<string, AddonSpec> {
            ["a"] = new AddonSpec {
                Name = "a",
                Git = AUrl,
                Rev = new string('a', 40),
                Path = "addons/a"
            },
        };

        var result = await resolver.ResolveGraphAsync(rootAddons, NoOverrides());

        Assert.Equal(2, result.Addons.Count); // a and b, no infinite expansion
    }
}
