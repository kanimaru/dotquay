using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

public class RepoCacheTests {
    [Fact]
    public async Task GetOrFetchAsync_FetchesOnFirstCall() {
        var fileSystem = new InMemoryFileSystem();
        var git = new FakeGitClient();
        var cache = new RepoCache(fileSystem, git, cacheRoot: "cache");

        var path = await cache.GetOrFetchAsync("https://example.com/foo", "abc123");

        Assert.Single(git.FetchCalls);
        Assert.Equal(("https://example.com/foo", "abc123", path), git.FetchCalls[0]);
    }

    [Fact]
    public async Task GetOrFetchAsync_DoesNotRefetchOnCacheHit() {
        var fileSystem = new InMemoryFileSystem();
        var git = new FakeGitClient();
        var cache = new RepoCache(fileSystem, git, cacheRoot: "cache");

        await cache.GetOrFetchAsync("https://example.com/foo", "abc123");
        await cache.GetOrFetchAsync("https://example.com/foo", "abc123");

        Assert.Single(git.FetchCalls); // second call was a cache hit, no fetch
    }

    [Fact]
    public async Task GetOrFetchAsync_RefetchesWhenPreviousFetchWasIncomplete() {
        var fileSystem = new InMemoryFileSystem();
        var git = new FakeGitClient();
        var cache = new RepoCache(fileSystem, git, cacheRoot: "cache");

        var path = cache.GetCachePath("https://example.com/foo", "abc123");
        // Simulate a crashed/interrupted fetch: files exist, but no completion marker.
        fileSystem.WriteAllText(fileSystem.CombinePath(path, "partial.txt"), "oops");

        await cache.GetOrFetchAsync("https://example.com/foo", "abc123");

        Assert.Single(git.FetchCalls);
        Assert.False(fileSystem.FileExists(fileSystem.CombinePath(path, "partial.txt")));
    }

    [Fact]
    public void GetCachePath_IsDeterministicAndDistinguishesRepos() {
        var fileSystem = new InMemoryFileSystem();
        var git = new FakeGitClient();
        var cache = new RepoCache(fileSystem, git, cacheRoot: "cache");

        var pathA1 = cache.GetCachePath("https://example.com/a", "abc123");
        var pathA2 = cache.GetCachePath("https://example.com/a", "abc123");
        var pathB = cache.GetCachePath("https://example.com/b", "abc123");

        Assert.Equal(pathA1, pathA2);
        Assert.NotEqual(pathA1, pathB);
    }

    [Fact]
    public void GetCachePath_UsesShortShaPrefix() {
        var fileSystem = new InMemoryFileSystem();
        var git = new FakeGitClient();
        var cache = new RepoCache(fileSystem, git, cacheRoot: "cache");

        var fullSha = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
        var path = cache.GetCachePath("https://example.com/a", fullSha);

        Assert.DoesNotContain(fullSha, path); // full 40-char sha would make Windows paths unwieldy
    }
}
