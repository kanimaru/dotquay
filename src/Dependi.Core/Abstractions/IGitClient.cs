using System.Threading;

namespace Dependi.Core.Abstractions;

/// <summary>
/// Shells out to <c>git</c> on PATH (SPEC.md §12 — real git handles SSH keys, credential helpers,
/// and proxies for free). Kept behind an interface so the resolver and sync engine are testable
/// without a network or a real git binary.
/// </summary>
public interface IGitClient
{
    /// <summary>
    /// Lists remote tags and branch heads without cloning. Backs <c>version</c>/<c>branch</c>
    /// resolution (SPEC.md §7.1) and <c>dependi outdated</c> (SPEC.md §6, requirement P1-3).
    /// </summary>
    Task<IReadOnlyList<GitRef>> ListRemoteRefsAsync(string repositoryUrl, CancellationToken cancellationToken = default);

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

    /// <summary>Resolves a tracked branch's current tip to a full commit SHA.</summary>
    Task<string> ResolveBranchTipAsync(string repositoryUrl, string branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the remote's default branch name (what <c>HEAD</c> points to), e.g. <c>"main"</c>.
    /// Backs <c>dependi add</c>'s untagged-repo fallback: "no tags found -> pin default-branch tip
    /// as rev" (SPEC.md §6) needs to know which branch that is before it can pin its tip.
    /// </summary>
    Task<string> ResolveDefaultBranchAsync(string repositoryUrl, CancellationToken cancellationToken = default);

    /// <summary>Checks whether <c>git</c> is installed and on PATH — no network access, no
    /// repository needed. Backs <c>dependi doctor</c>'s "git on PATH" sanity check (SPEC.md §6,
    /// requirement P1-4).</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>One ref reported by <c>git ls-remote</c>.</summary>
public sealed record GitRef(string Name, string CommitSha, GitRefKind Kind);

public enum GitRefKind
{
    Tag,
    Branch,
}
