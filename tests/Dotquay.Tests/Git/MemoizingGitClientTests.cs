using System.Collections.Concurrent;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Git;
using Xunit;

namespace Dotquay.Tests.Git;

public class MemoizingGitClientTests {
    private const string UrlA = "https://example.com/a.git";
    private const string UrlB = "https://example.com/b.git";

    [Fact]
    public async Task ListRemoteRefs_SameUrlTwice_AsksTheRemoteOnce() {
        var inner = new ScriptedGitClient();
        var client = new MemoizingGitClient(inner);

        var first = await client.ListRemoteRefsAsync(UrlA);
        var second = await client.ListRemoteRefsAsync(UrlA);

        Assert.Same(first, second);
        Assert.Equal(1, inner.CallCount($"ls-remote {UrlA}"));
    }

    [Fact]
    public async Task ListRemoteRefs_DifferentUrls_AreAskedSeparately() {
        var inner = new ScriptedGitClient();
        var client = new MemoizingGitClient(inner);

        await client.ListRemoteRefsAsync(UrlA);
        await client.ListRemoteRefsAsync(UrlB);

        Assert.Equal(1, inner.CallCount($"ls-remote {UrlA}"));
        Assert.Equal(1, inner.CallCount($"ls-remote {UrlB}"));
    }

    [Fact]
    public async Task ListRemoteRefs_UrlsDifferingOnlyInCase_AreDifferentQueries() {
        // A git URL's path can be case-sensitive on the server; treating these as one remote could
        // answer for the wrong repository.
        var inner = new ScriptedGitClient();
        var client = new MemoizingGitClient(inner);

        await client.ListRemoteRefsAsync(UrlA);
        await client.ListRemoteRefsAsync(UrlA.ToUpperInvariant());

        Assert.Equal(2, inner.TotalCalls);
    }

    [Fact]
    public async Task BranchTip_IsKeyedByUrlAndBranch() {
        var inner = new ScriptedGitClient();
        var client = new MemoizingGitClient(inner);

        var main1 = await client.ResolveBranchTipAsync(UrlA, "main");
        var main2 = await client.ResolveBranchTipAsync(UrlA, "main");
        var dev = await client.ResolveBranchTipAsync(UrlA, "dev");

        Assert.Equal(main1, main2);
        Assert.NotEqual(main1, dev);
        Assert.Equal(1, inner.CallCount($"tip {UrlA}@main"));
        Assert.Equal(1, inner.CallCount($"tip {UrlA}@dev"));
    }

    [Fact]
    public async Task DefaultBranch_IsMemoized() {
        var inner = new ScriptedGitClient();
        var client = new MemoizingGitClient(inner);

        await client.ResolveDefaultBranchAsync(UrlA);
        await client.ResolveDefaultBranchAsync(UrlA);

        Assert.Equal(1, inner.CallCount($"default {UrlA}"));
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneInFlightQuery() {
        var gate = new TaskCompletionSource<IReadOnlyList<GitRef>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ScriptedGitClient { RemoteRefs = _ => gate.Task };
        var client = new MemoizingGitClient(inner);

        var first = client.ListRemoteRefsAsync(UrlA);
        var second = client.ListRemoteRefsAsync(UrlA);
        gate.SetResult([new GitRef("v1.0.0", new string('a', 40), GitRefKind.Tag)]);

        Assert.Same(await first, await second);
        Assert.Equal(1, inner.CallCount($"ls-remote {UrlA}"));
    }

    [Fact]
    public async Task Failure_IsRemembered_SoARefusingRemoteIsNotAskedAgain() {
        var failure = new GitCommandException("Permission denied (publickey).");
        var inner = new ScriptedGitClient { RemoteRefs = _ => Task.FromException<IReadOnlyList<GitRef>>(failure) };
        var client = new MemoizingGitClient(inner);

        var first = await Assert.ThrowsAsync<GitCommandException>(() => client.ListRemoteRefsAsync(UrlA));
        var second = await Assert.ThrowsAsync<GitCommandException>(() => client.ListRemoteRefsAsync(UrlA));

        Assert.Same(failure, first);
        Assert.Same(failure, second);
        Assert.Equal(1, inner.CallCount($"ls-remote {UrlA}"));
    }

    [Fact]
    public async Task CancelledQuery_IsForgotten_SoTheNextCallerAsksAgain() {
        var attempts = 0;
        var inner = new ScriptedGitClient {
            RemoteRefs = _ => ++attempts == 1
                ? Task.FromCanceled<IReadOnlyList<GitRef>>(new CancellationToken(canceled: true))
                : Task.FromResult<IReadOnlyList<GitRef>>([]),
        };
        var client = new MemoizingGitClient(inner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListRemoteRefsAsync(UrlA));
        var retried = await client.ListRemoteRefsAsync(UrlA);

        Assert.Empty(retried);
        Assert.Equal(2, inner.CallCount($"ls-remote {UrlA}"));
    }

    [Fact]
    public async Task CallerWhoStopsWaiting_DoesNotCancelTheSharedQueryForOthers() {
        var gate = new TaskCompletionSource<IReadOnlyList<GitRef>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ScriptedGitClient { RemoteRefs = _ => gate.Task };
        var client = new MemoizingGitClient(inner);
        using var impatient = new CancellationTokenSource();

        var abandoned = client.ListRemoteRefsAsync(UrlA, impatient.Token);
        var patient = client.ListRemoteRefsAsync(UrlA);
        await impatient.CancelAsync();
        gate.SetResult([]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Empty(await patient);
        Assert.Equal(1, inner.CallCount($"ls-remote {UrlA}"));
    }

    [Fact]
    public async Task DefaultInterfaceMembers_AreForwarded_NotLeftAtTheirNoOpDefaults() {
        // GetCommitDateAsync and EnsureFilesResolvedAsync have default bodies on IGitClient. A wrapper
        // that forgot to forward them would compile, and quietly turn off LFS downloads and commit-dated
        // pseudo-versions for every addon resolved through it.
        var date = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var inner = new ScriptedGitClient { CommitDate = date };
        IGitClient client = new MemoizingGitClient(inner);

        Assert.Equal(date, await client.GetCommitDateAsync("checkout", "sha"));
        await client.EnsureFilesResolvedAsync("checkout");

        Assert.Equal(1, inner.CallCount("commit-date checkout sha"));
        Assert.Equal(1, inner.CallCount("lfs checkout"));
    }

    [Fact]
    public async Task LocalAndWritingCalls_AreNeverMemoized() {
        var inner = new ScriptedGitClient();
        var client = new MemoizingGitClient(inner);

        await client.FetchCommitAsync(UrlA, "sha", "dest");
        await client.FetchCommitAsync(UrlA, "sha", "dest");
        await client.IsAvailableAsync();
        await client.IsAvailableAsync();

        Assert.Equal(2, inner.CallCount($"fetch {UrlA} sha dest"));
        Assert.Equal(2, inner.CallCount("available"));
    }

    [Fact]
    public void Wrap_DoesNotStackASecondCache() {
        var once = MemoizingGitClient.Wrap(new ScriptedGitClient());

        Assert.Same(once, MemoizingGitClient.Wrap(once));
    }

    /// <summary>Records every call by a readable key; each query answers something distinct per key so a
    /// mixed-up cache would show as a wrong value, not only as a wrong count.</summary>
    private sealed class ScriptedGitClient : IGitClient {
        private readonly ConcurrentQueue<string> _calls = new();

        public Func<string, Task<IReadOnlyList<GitRef>>>? RemoteRefs { get; init; }

        public DateTimeOffset? CommitDate { get; init; }

        public int TotalCalls => _calls.Count;

        public int CallCount(string key) => _calls.Count(c => c == key);

        public Task<IReadOnlyList<GitRef>> ListRemoteRefsAsync(string repositoryUrl, CancellationToken cancellationToken = default) {
            _calls.Enqueue($"ls-remote {repositoryUrl}");
            return RemoteRefs?.Invoke(repositoryUrl)
                ?? Task.FromResult<IReadOnlyList<GitRef>>([new GitRef("v1.0.0", Sha(repositoryUrl), GitRefKind.Tag)]);
        }

        public Task<string> ResolveBranchTipAsync(string repositoryUrl, string branch, CancellationToken cancellationToken = default) {
            _calls.Enqueue($"tip {repositoryUrl}@{branch}");
            return Task.FromResult(Sha(repositoryUrl + "@" + branch));
        }

        public Task<string> ResolveDefaultBranchAsync(string repositoryUrl, CancellationToken cancellationToken = default) {
            _calls.Enqueue($"default {repositoryUrl}");
            return Task.FromResult("main");
        }

        public Task FetchCommitAsync(string repositoryUrl, string commitSha, string destinationPath, CancellationToken cancellationToken = default) {
            _calls.Enqueue($"fetch {repositoryUrl} {commitSha} {destinationPath}");
            return Task.CompletedTask;
        }

        public Task EnsureFilesResolvedAsync(string checkoutPath, CancellationToken cancellationToken = default) {
            _calls.Enqueue($"lfs {checkoutPath}");
            return Task.CompletedTask;
        }

        public Task<DateTimeOffset?> GetCommitDateAsync(string checkoutPath, string commitSha, CancellationToken cancellationToken = default) {
            _calls.Enqueue($"commit-date {checkoutPath} {commitSha}");
            return Task.FromResult(CommitDate);
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) {
            _calls.Enqueue("available");
            return Task.FromResult(true);
        }

        private static string Sha(string seed) =>
            Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
    }
}
