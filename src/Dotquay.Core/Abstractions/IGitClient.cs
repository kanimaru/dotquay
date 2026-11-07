using System.Threading;

namespace Dotquay.Core.Abstractions;

/// <summary>
/// Shells out to <c>git</c> on PATH (SPEC.md §12 — real git handles SSH keys, credential helpers,
/// and proxies for free). Kept behind an interface so the resolver and sync engine are testable
/// without a network or a real git binary.
/// </summary>
public interface IGitClient {
    /// <summary>
    /// Lists remote tags and branch heads without cloning. Backs <c>version</c>/<c>branch</c>
    /// resolution (SPEC.md §7.1) and <c>dotquay outdated</c> (SPEC.md §6, requirement P1-3).
    /// </summary>
    Task<IReadOnlyList<GitRef>> ListRemoteRefsAsync(string repositoryUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single commit into <paramref name="destinationPath"/>. Shallow fetch is the
    /// baseline, not an optimization; implementations fall back to a full clone only when the
    /// server refuses to shallow-fetch the given SHA (SPEC.md §9 step 1, §12, decided OQ-4).
    /// </summary>
    Task FetchCommitAsync(
        string repositoryUrl,
        string commitSha,
        string destinationPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes sure a checkout this client produced holds real files, not Git LFS pointers: downloads
    /// them when it can, and throws a message that names the fix when it cannot. A repository
    /// without LFS costs one search and nothing else. <see cref="FetchCommitAsync"/> does this
    /// itself; this exists for a cache entry made <em>before</em> that was true, which would
    /// otherwise be trusted forever.
    /// </summary>
    /// <remarks>Defaults to doing nothing so implementations with no real checkout (test doubles)
    /// need not care.</remarks>
    Task EnsureFilesResolvedAsync(string checkoutPath,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Reads when <paramref name="commitSha"/> was committed, from a checkout this client produced
    /// — local only, no network. Dates an untagged commit's pseudo-version, which has to name the
    /// commit rather than the day it happened to be installed, or reinstalling the same commit on
    /// another day rewrites the lockfile.
    /// </summary>
    /// <returns>The committer date, or null when the checkout cannot say.</returns>
    /// <remarks>Defaults to null so implementations with no real checkout (test doubles) need not
    /// care.</remarks>
    Task<DateTimeOffset?> GetCommitDateAsync(
        string checkoutPath, string commitSha, CancellationToken cancellationToken = default)
        => Task.FromResult<DateTimeOffset?>(null);

    /// <summary>Resolves a tracked branch's current tip to a full commit SHA.</summary>
    Task<string> ResolveBranchTipAsync(string repositoryUrl, string branch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the remote's default branch name (what <c>HEAD</c> points to), e.g. <c>"main"</c>.
    /// Backs <c>dotquay add</c>'s untagged-repo fallback: "no tags found -> pin default-branch tip
    /// as rev" (SPEC.md §6) needs to know which branch that is before it can pin its tip.
    /// </summary>
    Task<string> ResolveDefaultBranchAsync(string repositoryUrl,
        CancellationToken cancellationToken = default);

    /// <summary>Checks whether <c>git</c> is installed and on PATH — no network access, no
    /// repository needed. Backs <c>dotquay doctor</c>'s "git on PATH" sanity check (SPEC.md §6,
    /// requirement P1-4).</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>One ref reported by <c>git ls-remote</c>.</summary>
public sealed record GitRef(string Name, string CommitSha, GitRefKind Kind);

public enum GitRefKind {
    Tag,
    Branch,
}
