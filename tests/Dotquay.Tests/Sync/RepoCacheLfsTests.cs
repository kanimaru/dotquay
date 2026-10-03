using Dotquay.Core.Abstractions;
using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

/// <summary>
/// The fetch cache must not trust a checkout forever just because its completion marker exists: an entry
/// made before dotquay checked for Git LFS pointers can hold placeholders where binaries belong, and would
/// keep handing them out on every install, on every project, without ever touching the network again.
/// </summary>
public class RepoCacheLfsTests
{
    private const string Url = "https://example.com/gdmp.git";
    private const string Sha = "5f977e7ac86903e970c556cc93d7d05e542b68f0";

    private sealed class RecordingGit : IGitClient
    {
        public int Fetches, Verifications;
        public Exception? VerifyThrows;

        public Task FetchCommitAsync(string repositoryUrl, string commitSha, string destinationPath, CancellationToken cancellationToken = default)
        {
            Fetches++;
            return Task.CompletedTask;
        }

        public Task EnsureFilesResolvedAsync(string checkoutPath, CancellationToken cancellationToken = default)
        {
            Verifications++;
            return VerifyThrows is null ? Task.CompletedTask : Task.FromException(VerifyThrows);
        }

        public Task<IReadOnlyList<GitRef>> ListRemoteRefsAsync(string repositoryUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ResolveBranchTipAsync(string repositoryUrl, string branch, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ResolveDefaultBranchAsync(string repositoryUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private static (RepoCache Cache, RecordingGit Git, InMemoryFileSystem Fs) Build()
    {
        var fs = new InMemoryFileSystem();
        var git = new RecordingGit();
        return (new RepoCache(fs, git, "cache"), git, fs);
    }

    [Fact]
    public async Task AFreshFetch_IsMarkedVerified_SoTheNextUseDoesNotCheckAgain()
    {
        var (cache, git, _) = Build();

        await cache.GetOrFetchAsync(Url, Sha);
        await cache.GetOrFetchAsync(Url, Sha);
        await cache.GetOrFetchAsync(Url, Sha);

        Assert.Equal(1, git.Fetches);
        Assert.Equal(0, git.Verifications); // the fetch itself resolved LFS; a verified entry is trusted
    }

    [Fact]
    public async Task ALegacyEntry_WithTheShaAloneInItsMarker_IsVerifiedOnceThenMarked()
    {
        var (cache, git, fs) = Build();
        var path = cache.GetCachePath(Url, Sha);
        fs.WriteAllText(fs.CombinePath(path, ".dotquay-fetch-complete"), Sha); // what older dotquay wrote

        await cache.GetOrFetchAsync(Url, Sha);
        await cache.GetOrFetchAsync(Url, Sha);

        Assert.Equal(0, git.Fetches);       // no new download: it is verified in place
        Assert.Equal(1, git.Verifications); // exactly once, then the entry is marked
    }

    [Fact]
    public async Task ALegacyEntryThatCannotBeVerified_FailsLoudlyInsteadOfBeingReused()
    {
        var (cache, git, fs) = Build();
        git.VerifyThrows = new InvalidOperationException("git-lfs is not installed");
        fs.WriteAllText(fs.CombinePath(cache.GetCachePath(Url, Sha), ".dotquay-fetch-complete"), Sha);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrFetchAsync(Url, Sha));

        // And it is not marked verified by having been looked at: the next attempt checks again.
        git.VerifyThrows = null;
        await cache.GetOrFetchAsync(Url, Sha);
        Assert.Equal(2, git.Verifications);
    }
}
