using System.Collections.Concurrent;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Diagnostics;

namespace Dotquay.Core.Git;

/// <summary>
/// Answers each remote ref query at most once for as long as this instance lives, by remembering the
/// <em>task</em> of the first call and handing the same task to every later caller.
/// </summary>
/// <remarks>
/// <para>
/// Exists because one graph resolution asks the same question several times. The walk resolves every
/// addon to peek at its package manifest, then <see cref="Resolve.AddonUnifier"/> resolves it again to
/// pick a commit - measured on vbeami, that was eight <c>git ls-remote</c> calls for four addons, about
/// two seconds of ssh handshakes spent re-learning answers dotquay already had.
/// </para>
/// <para>
/// <b>Scope is the whole point.</b> Refs move; a cache that outlives one operation would serve a
/// branch tip from before somebody pushed. So this is never a process-wide singleton: whoever owns one
/// resolution (today <see cref="Resolve.DependencyGraphResolver"/>) creates one and drops it with the
/// resolution. In particular <c>dotquay mcp</c> keeps its workflow alive across many tool calls, and
/// must not see stale refs because of this type.
/// </para>
/// <para>
/// The task is stored rather than the result so concurrent callers share one in-flight git process
/// instead of racing to start two - which is what makes <see cref="Resolve.RefPrefetcher"/> work: it
/// starts the queries early, and the walk later awaits the very same tasks.
/// </para>
/// <para>
/// A <em>failure</em> is remembered like an answer. A remote that refused once would refuse again
/// within the same second, and asking twice only doubles the time to the same error message.
/// Cancellation is the exception: a cancelled query is forgotten, so it never poisons a later caller
/// with somebody else's cancellation.
/// </para>
/// </remarks>
public sealed class MemoizingGitClient : IGitClient
{
    private readonly IGitClient _inner;

    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<GitRef>>>> _remoteRefs =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<(string Url, string Branch), Lazy<Task<string>>> _branchTips = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _defaultBranches =
        new(StringComparer.Ordinal);

    public MemoizingGitClient(IGitClient inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>Wraps <paramref name="gitClient"/> unless it already memoizes, so nesting two owners
    /// never stacks two caches with different lifetimes.</summary>
    public static MemoizingGitClient Wrap(IGitClient gitClient) =>
        gitClient as MemoizingGitClient ?? new MemoizingGitClient(gitClient);

    public Task<IReadOnlyList<GitRef>> ListRemoteRefsAsync(string repositoryUrl, CancellationToken cancellationToken = default) =>
        Memoize(
            _remoteRefs,
            repositoryUrl,
            url => _inner.ListRemoteRefsAsync(url, cancellationToken),
            $"ls-remote {repositoryUrl}",
            cancellationToken);

    public Task<string> ResolveBranchTipAsync(string repositoryUrl, string branch, CancellationToken cancellationToken = default) =>
        Memoize<(string Url, string Branch), string>(
            _branchTips,
            (Url: repositoryUrl, Branch: branch),
            key => _inner.ResolveBranchTipAsync(key.Url, key.Branch, cancellationToken),
            $"branch tip {repositoryUrl}@{branch}",
            cancellationToken);

    public Task<string> ResolveDefaultBranchAsync(string repositoryUrl, CancellationToken cancellationToken = default) =>
        Memoize(
            _defaultBranches,
            repositoryUrl,
            url => _inner.ResolveDefaultBranchAsync(url, cancellationToken),
            $"default branch {repositoryUrl}",
            cancellationToken);

    // Everything below is local, or writes, or both - nothing a second call could answer from memory.
    // EnsureFilesResolvedAsync and GetCommitDateAsync are default interface members: forwarding them
    // explicitly is load-bearing. Leaving them out would silently pick up the no-op defaults and turn
    // off LFS resolution and pseudo-version dating for anyone holding this wrapper.

    public Task FetchCommitAsync(
        string repositoryUrl, string commitSha, string destinationPath, CancellationToken cancellationToken = default) =>
        _inner.FetchCommitAsync(repositoryUrl, commitSha, destinationPath, cancellationToken);

    public Task EnsureFilesResolvedAsync(string checkoutPath, CancellationToken cancellationToken = default) =>
        _inner.EnsureFilesResolvedAsync(checkoutPath, cancellationToken);

    public Task<DateTimeOffset?> GetCommitDateAsync(
        string checkoutPath, string commitSha, CancellationToken cancellationToken = default) =>
        _inner.GetCommitDateAsync(checkoutPath, commitSha, cancellationToken);

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        _inner.IsAvailableAsync(cancellationToken);

    private static async Task<T> Memoize<TKey, T>(
        ConcurrentDictionary<TKey, Lazy<Task<T>>> memo,
        TKey key,
        Func<TKey, Task<T>> query,
        string description,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        var entry = memo.GetOrAdd(key, k => new Lazy<Task<T>>(() => query(k), LazyThreadSafetyMode.ExecutionAndPublication));

        if (entry.IsValueCreated)
        {
            DiagnosticLog.Write(DiagnosticLevel.Verbose, "git", $"reusing {description} from earlier in this run");
        }

        var task = entry.Value;

        try
        {
            // WaitAsync: a caller who cancels stops waiting without cancelling the shared query for
            // everyone else still waiting on it.
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (task.IsCanceled)
        {
            // Only the query's own cancellation is forgotten. Removing by (key, entry) leaves alone a
            // retry that has already replaced this entry.
            memo.TryRemove(new KeyValuePair<TKey, Lazy<Task<T>>>(key, entry));
            throw;
        }
    }
}
