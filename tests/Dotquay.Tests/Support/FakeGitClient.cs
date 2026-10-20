using Dotquay.Core.Abstractions;

namespace Dotquay.Tests.Support;

/// <summary>Configurable <see cref="IGitClient"/> test double — no real git process or network
/// call needed to test the resolver (SPEC.md §12: testable without network/disk).</summary>
public sealed class FakeGitClient : IGitClient
{
    public Dictionary<string, IReadOnlyList<GitRef>> RemoteRefsByUrl { get; } = new();

    public Dictionary<(string Url, string Branch), string> BranchTips { get; } = new();

    public Dictionary<string, string> DefaultBranches { get; } = new();

    public List<(string Url, string Sha, string Destination)> FetchCalls { get; } = [];

    /// <summary>Optional: when set alongside <see cref="FetchedFilesByUrl"/>, <see cref="FetchCommitAsync"/>
    /// writes the configured fake repo content into the destination path, so tests further down the
    /// pipeline (subtree extraction, sync) have something real to work with instead of an empty
    /// "fetched" directory.</summary>
    public IFileSystem? FileSystemForFetchedContent { get; set; }

    public Dictionary<string, IReadOnlyDictionary<string, string>> FetchedFilesByUrl { get; } = new();

    /// <summary>Same idea as <see cref="FetchedFilesByUrl"/> but keyed by (url, sha) — needed
    /// whenever a test cares that two different commits of the same repo have different content
    /// (e.g. patch re-apply: the old base a patch was captured against vs. the new base it's being
    /// re-applied on top of). Checked first; falls back to <see cref="FetchedFilesByUrl"/> when no
    /// (url, sha) entry exists, so existing single-commit tests are unaffected.</summary>
    public Dictionary<(string Url, string Sha), IReadOnlyDictionary<string, string>> FetchedFilesByUrlAndSha { get; } = new();

    /// <summary>Backs <see cref="IsAvailableAsync"/> — defaults to true (git present), so only tests
    /// specifically about the "git missing" doctor check need to set this false.</summary>
    public bool IsGitAvailable { get; set; } = true;

    /// <summary>Backs <see cref="GetCommitDateAsync"/>, keyed by commit SHA. A SHA with no entry has no
    /// date, as for a checkout git can't read.</summary>
    public Dictionary<string, DateTimeOffset> CommitDates { get; } = new();

    public List<(string CheckoutPath, string Sha)> CommitDateCalls { get; } = [];

    public Task<DateTimeOffset?> GetCommitDateAsync(
        string checkoutPath, string commitSha, CancellationToken cancellationToken = default)
    {
        CommitDateCalls.Add((checkoutPath, commitSha));
        return Task.FromResult<DateTimeOffset?>(CommitDates.TryGetValue(commitSha, out var date) ? date : null);
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsGitAvailable);

    /// <summary>Remotes that refuse: <see cref="ListRemoteRefsAsync"/> throws the configured exception
    /// for these URLs, as the real client does when git exits non-zero.</summary>
    public Dictionary<string, Exception> RemoteFailuresByUrl { get; } = new();

    public Task<IReadOnlyList<GitRef>> ListRemoteRefsAsync(string repositoryUrl, CancellationToken cancellationToken = default) =>
        RemoteFailuresByUrl.TryGetValue(repositoryUrl, out var failure)
            ? Task.FromException<IReadOnlyList<GitRef>>(failure)
            : Task.FromResult(RemoteRefsByUrl.TryGetValue(repositoryUrl, out var refs) ? refs : []);

    public Task<string> ResolveBranchTipAsync(string repositoryUrl, string branch, CancellationToken cancellationToken = default)
    {
        if (!BranchTips.TryGetValue((repositoryUrl, branch), out var sha))
        {
            throw new InvalidOperationException($"No fake branch tip configured for {repositoryUrl}@{branch}.");
        }

        return Task.FromResult(sha);
    }

    public Task<string> ResolveDefaultBranchAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        if (!DefaultBranches.TryGetValue(repositoryUrl, out var branch))
        {
            throw new InvalidOperationException($"No fake default branch configured for {repositoryUrl}.");
        }

        return Task.FromResult(branch);
    }

    public Task FetchCommitAsync(
        string repositoryUrl, string commitSha, string destinationPath, CancellationToken cancellationToken = default)
    {
        FetchCalls.Add((repositoryUrl, commitSha, destinationPath));

        if (FileSystemForFetchedContent is { } fileSystem)
        {
            var files = FetchedFilesByUrlAndSha.TryGetValue((repositoryUrl, commitSha), out var perSha)
                ? perSha
                : FetchedFilesByUrl.TryGetValue(repositoryUrl, out var perUrl) ? perUrl : null;

            if (files is not null)
            {
                foreach (var (relativePath, content) in files)
                {
                    fileSystem.WriteAllText(fileSystem.CombinePath(destinationPath, relativePath), content);
                }
            }
        }

        return Task.CompletedTask;
    }
}
