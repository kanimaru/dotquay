using System.Collections.Concurrent;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Git;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Resolve;

/// <summary>
/// How many remote lookups one graph resolution makes, and how many at once - the two things that
/// decided how long a no-op <c>update</c> took (eight sequential ssh handshakes for four addons, on
/// vbeami).
/// </summary>
public class GraphRemoteQueryTests {
    private const string ToolUrl = "https://example.com/tool.git";
    private const string UiUrl = "https://example.com/ui.git";
    private const string CoreUrl = "https://example.com/core.git";
    private const string UiTip = "cccccccccccccccccccccccccccccccccccccccc";

    private static Dictionary<string, AddonSpec> NoOverrides() => new();

    private static (DependencyGraphResolver Resolver, ObservedGitClient Git,
        FakeGitClient Fake) Build(TimeSpan? latency = null) {
        var fileSystem = new InMemoryFileSystem();
        var fake = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        var observed = new ObservedGitClient(fake, latency ?? TimeSpan.Zero);
        var cache = new RepoCache(fileSystem, observed, "cache");
        return (new DependencyGraphResolver(fileSystem, observed, cache, "discovery"), observed,
            fake);
    }

    private static void SetTags(FakeGitClient git, string url, params string[] tags) =>
        git.RemoteRefsByUrl[url] = tags.Select((tag, i) =>
            new GitRef(tag, new string('a', 39) + i, GitRefKind.Tag)).ToList();

    private static string DependsOnCore(string name) => $"""
        [package]
        name = "{name}"
        version = "1.0.0"

        [addons.core]
        git = "{CoreUrl}"
        version = "~1.0"
        """;

    /// <summary>tool (version) and ui (branch) both depend on core - the shape where discovery and
    /// unification each used to ask every remote again.</summary>
    private static Dictionary<string, AddonSpec> SetUpDiamond(FakeGitClient git) {
        SetTags(git, ToolUrl, "v1.0.0");
        git.FetchedFilesByUrl[ToolUrl] = new Dictionary<string, string> {
            ["addons/tool/plugin.cfg"] = "tool",
            ["addons/tool/dotquay.toml"] = DependsOnCore("tool"),
        };

        git.BranchTips[(UiUrl, "main")] = UiTip;
        git.FetchedFilesByUrl[UiUrl] = new Dictionary<string, string> {
            ["addons/ui/plugin.cfg"] = "ui",
            ["addons/ui/dotquay.toml"] = DependsOnCore("ui"),
        };

        SetTags(git, CoreUrl, "v1.0.0", "v1.0.3", "v1.1.0");
        git.FetchedFilesByUrl[CoreUrl] = new Dictionary<string,
            string> { ["addons/core/plugin.cfg"] = "core" };

        return new Dictionary<string, AddonSpec> {
            ["tool"] = new AddonSpec {
                Name = "tool",
                Git = ToolUrl,
                Version = "~1.0",
                Path = "addons/tool"
            },
            ["ui"] = new AddonSpec {
                Name = "ui",
                Git = UiUrl,
                Branch = "main",
                Path = "addons/ui"
            },
        };
    }

    [Fact]
    public async Task ResolveGraph_AsksEachRemoteOnce_ThoughDiscoveryAndUnificationBothResolve() {
        var (resolver, git, fake) = Build();
        var roots = SetUpDiamond(fake);

        var result = await resolver.ResolveGraphAsync(roots, NoOverrides());

        Assert.Equal(["core", "tool", "ui"], result.Addons.Select(a => a.Resolved.Name).Order());
        Assert.Equal(1, git.CallCount($"ls-remote {ToolUrl}"));
        Assert.Equal(1, git.CallCount($"tip {UiUrl}@main"));

        // Required by two addons, discovered once, unified once: still one lookup.
        Assert.Equal(1, git.CallCount($"ls-remote {CoreUrl}"));
    }

    [Fact]
    public async Task ResolveGraph_ResultIsUnchangedByMemoizationAndPrefetch() {
        // The same graph through a plain, uncached resolver path: each addon resolved on its own.
        var (resolver, _, fake) = Build();
        var roots = SetUpDiamond(fake);
        var direct = new AddonResolver(fake);

        var result = await resolver.ResolveGraphAsync(roots, NoOverrides());

        Assert.Equal((await direct.ResolveAsync(roots["tool"])).ResolvedSha, Single(result, "tool")
            .ResolvedSha);
        Assert.Equal(UiTip, Single(result, "ui").ResolvedSha);
        Assert.Equal("v1.0.3", Single(result, "core").DisplayVersion);
    }

    [Fact]
    public async Task ResolveGraph_SeparateResolutions_DoNotShareAnswers() {
        // One resolver per resolution is the cache's whole lifetime: a branch moved between two
        // updates must be seen by the second one.
        var fileSystem = new InMemoryFileSystem();
        var fake = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        var roots = SetUpDiamond(fake);
        var cache = new RepoCache(fileSystem, fake, "cache");

        var before = await new DependencyGraphResolver(fileSystem, fake, cache, "discovery")
            .ResolveGraphAsync(roots, NoOverrides());
        fake.BranchTips[(UiUrl, "main")] = "dddddddddddddddddddddddddddddddddddddddd";
        var after = await new DependencyGraphResolver(fileSystem, fake, cache, "discovery")
            .ResolveGraphAsync(roots, NoOverrides());

        Assert.Equal(UiTip, Single(before, "ui").ResolvedSha);
        Assert.Equal("dddddddddddddddddddddddddddddddddddddddd", Single(after, "ui").ResolvedSha);
    }

    [Fact]
    public async Task ResolveGraph_LooksUpRootsConcurrently() {
        var (resolver, git, fake) = Build(latency: TimeSpan.FromMilliseconds(150));
        var roots = new Dictionary<string, AddonSpec>();
        foreach (var name in new[] { "a", "b", "c", "d" }) {
            var url = $"https://example.com/{name}.git";
            SetTags(fake, url, "v1.0.0");
            fake.FetchedFilesByUrl[url] = new Dictionary<string,
                string> { [$"addons/{name}/plugin.cfg"] = name };
            roots[name] = new AddonSpec {
                Name = name,
                Git = url,
                Version = "~1.0",
                Path = $"addons/{name}"
            };
        }

        await resolver.ResolveGraphAsync(roots, NoOverrides());

        Assert.True(git.MaxInFlight >= 2,
            $"expected overlapping lookups, saw at most {git.MaxInFlight} at once");
        Assert.Equal(4, git.TotalLookups);
    }

    [Fact]
    public async Task ResolveGraph_CapsConcurrentLookups() {
        var (resolver, git, fake) = Build(latency: TimeSpan.FromMilliseconds(60));
        var roots = new Dictionary<string, AddonSpec>();
        for (var i = 0; i < 12; i++) {
            var name = $"addon{i}";
            var url = $"https://example.com/{name}.git";
            SetTags(fake, url, "v1.0.0");
            fake.FetchedFilesByUrl[url] = new Dictionary<string,
                string> { [$"addons/{name}/plugin.cfg"] = name };
            roots[name] = new AddonSpec {
                Name = name,
                Git = url,
                Version = "~1.0",
                Path = $"addons/{name}"
            };
        }

        await resolver.ResolveGraphAsync(roots, NoOverrides());

        // The prefetcher holds at most DefaultMaxConcurrency slots; the walk's own call for the
        // addon it is on can be the one extra, when it gets there before the prefetch for that
        // addon has started.
        Assert.InRange(git.MaxInFlight, 1, RefPrefetcher.DefaultMaxConcurrency + 1);
        Assert.Equal(12, git.TotalLookups);
    }

    [Fact]
    public async Task ResolveGraph_RevPinnedRoots_MakeNoLookupsAtAll() {
        var (resolver, git, fake) = Build();
        fake.FetchedFilesByUrl[ToolUrl] = new Dictionary<string,
            string> { ["addons/tool/plugin.cfg"] = "tool" };
        var roots = new Dictionary<string, AddonSpec> {
            ["tool"] = new AddonSpec {
                Name = "tool",
                Git = ToolUrl,
                Rev = new string('e', 40),
                Path = "addons/tool"
            },
        };

        await resolver.ResolveGraphAsync(roots, NoOverrides());

        Assert.Equal(0, git.TotalLookups);
    }

    [Fact]
    public async Task ResolveGraph_FailingRemote_StillFails_AndIsAskedOnlyOnce() {
        // The prefetch swallows its copy of the failure; the walk must still see it, unchanged.
        var (resolver, git, fake) = Build();
        fake.RemoteFailuresByUrl[ToolUrl] = new GitCommandException("fatal: repository not found");
        var roots = new Dictionary<string, AddonSpec> {
            ["tool"] = new AddonSpec {
                Name = "tool",
                Git = ToolUrl,
                Version = "~1.0",
                Path = "addons/tool"
            },
        };

        var ex = await Assert.ThrowsAsync<GitCommandException>(() =>
            resolver.ResolveGraphAsync(roots, NoOverrides()));

        Assert.Equal("fatal: repository not found", ex.Message);
        Assert.Equal(1, git.CallCount($"ls-remote {ToolUrl}"));
    }

    [Fact]
    public async Task ResolveGraph_OverriddenRoot_IsPrefetchedWithTheOverride_NotTheRequestedSpec() {
        var (resolver, git, fake) = Build();
        var roots = SetUpDiamond(fake);
        fake.BranchTips[(ToolUrl, "next")] = "ffffffffffffffffffffffffffffffffffffffff";
        var overrides = new Dictionary<string, AddonSpec> {
            ["tool"] = new AddonSpec {
                Name = "tool",
                Git = ToolUrl,
                Branch = "next",
                Path = "addons/tool"
            },
        };

        var result = await resolver.ResolveGraphAsync(roots, overrides);

        Assert.Equal("ffffffffffffffffffffffffffffffffffffffff", Single(result, "tool")
            .ResolvedSha);
        Assert.Equal(0, git.CallCount($"ls-remote {ToolUrl}"));
    }

    private static ResolvedAddon Single(GraphResolutionResult result, string name) =>
        Assert.Single(result.Addons, a => a.Resolved.Name == name).Resolved;

    /// <summary>Counts and delays the remote lookups of a <see cref="FakeGitClient"/>, and tracks
    /// the most that were ever in flight at once.</summary>
    private sealed class ObservedGitClient(FakeGitClient inner, TimeSpan latency) : IGitClient {
        private readonly ConcurrentQueue<string> _calls = new();
        private int _inFlight;
        private int _maxInFlight;

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public int TotalLookups => _calls.Count;

        public int CallCount(string key) => _calls.Count(c => c == key);

        public Task<IReadOnlyList<GitRef>> ListRemoteRefsAsync(string repositoryUrl,
            CancellationToken cancellationToken = default) =>
            Observe($"ls-remote {repositoryUrl}", () =>
                inner.ListRemoteRefsAsync(repositoryUrl, cancellationToken));

        public Task<string> ResolveBranchTipAsync(string repositoryUrl, string branch,
            CancellationToken cancellationToken = default) =>
            Observe($"tip {repositoryUrl}@{branch}", () =>
                inner.ResolveBranchTipAsync(repositoryUrl, branch, cancellationToken));

        public Task<string> ResolveDefaultBranchAsync(string repositoryUrl,
            CancellationToken cancellationToken = default) =>
            Observe($"default {repositoryUrl}", () =>
                inner.ResolveDefaultBranchAsync(repositoryUrl, cancellationToken));

        public Task FetchCommitAsync(string repositoryUrl, string commitSha, string destinationPath,
            CancellationToken cancellationToken = default) {
            // FakeGitClient's fetch writes into a non-thread-safe in-memory file system, so it is
            // kept strictly to the walk's own flow - which is the only place fetches happen.
            return inner.FetchCommitAsync(repositoryUrl, commitSha, destinationPath,
                cancellationToken);
        }

        public Task<DateTimeOffset?> GetCommitDateAsync(string checkoutPath, string commitSha,
            CancellationToken cancellationToken = default) =>
            inner.GetCommitDateAsync(checkoutPath, commitSha, cancellationToken);

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            inner.IsAvailableAsync(cancellationToken);

        private async Task<T> Observe<T>(string key, Func<Task<T>> call) {
            _calls.Enqueue(key);
            var now = Interlocked.Increment(ref _inFlight);
            UpdateMax(now);
            try {
                if (latency > TimeSpan.Zero) {
                    await Task.Delay(latency);
                }

                return await call();
            } finally {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void UpdateMax(int candidate) {
            int seen;
            while (candidate > (seen = Volatile.Read(ref _maxInFlight))) {
                if (Interlocked.CompareExchange(ref _maxInFlight, candidate, seen) == seen) {
                    return;
                }
            }
        }
    }
}
