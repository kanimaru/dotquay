using System.Security.Cryptography;
using System.Text;
using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Sync;

/// <summary>
/// Global content-addressed fetch cache: <c>~/.dotquay/cache/&lt;repo-hash&gt;/&lt;short-sha&gt;/</c>
/// (SPEC.md §9 step 1, decided OQ-5 — hashed paths keep things short on Windows). Shared across
/// projects: fetching the same addon@SHA from two different projects only hits the network once
/// (requirement P0-8).
/// </summary>
public sealed class RepoCache
{
    // Written after a fetch completes successfully. Its presence — not just the directory's — is
    // what "already cached" means, so a fetch interrupted mid-way (crash, Ctrl-C) is retried
    // instead of being mistaken for a complete, usable cache entry.
    private const string CompletionMarkerFileName = ".dotquay-fetch-complete";

    // Added to the marker once the checkout is known to hold real files rather than Git LFS pointers.
    // Entries written before dotquay checked for that carry the SHA alone; they are verified once on
    // their next use and then marked, instead of being trusted forever. Not a new marker NAME: that
    // would throw away every cache entry on every machine to catch a few.
    private const string LfsVerifiedToken = "lfs-verified";

    private readonly IFileSystem _fileSystem;
    private readonly IGitClient _gitClient;
    private readonly string _cacheRoot;

    public RepoCache(IFileSystem fileSystem, IGitClient gitClient, string cacheRoot)
    {
        _fileSystem = fileSystem;
        _gitClient = gitClient;
        _cacheRoot = cacheRoot;
    }

    /// <summary>Returns the cached path for <paramref name="repositoryUrl"/>@<paramref name="commitSha"/>,
    /// fetching into it first if it isn't already there.</summary>
    public async Task<string> GetOrFetchAsync(string repositoryUrl, string commitSha, CancellationToken cancellationToken = default)
    {
        var path = GetCachePath(repositoryUrl, commitSha);
        var marker = _fileSystem.CombinePath(path, CompletionMarkerFileName);

        if (_fileSystem.FileExists(marker))
        {
            if (!_fileSystem.ReadAllText(marker).Contains(LfsVerifiedToken, StringComparison.Ordinal))
            {
                await _gitClient.EnsureFilesResolvedAsync(path, cancellationToken);
                _fileSystem.WriteAllText(marker, MarkerContent(commitSha));
            }

            return path;
        }

        _fileSystem.DeleteDirectory(path); // clear out any partial fetch from an interrupted run
        await _gitClient.FetchCommitAsync(repositoryUrl, commitSha, path, cancellationToken);
        _fileSystem.WriteAllText(marker, MarkerContent(commitSha));

        return path;
    }

    private static string MarkerContent(string commitSha) => $"{commitSha}\n{LfsVerifiedToken}";

    public string GetCachePath(string repositoryUrl, string commitSha)
    {
        var repoHash = ShortHash(repositoryUrl);
        var shortSha = commitSha.Length > 12 ? commitSha[..12] : commitSha;
        return _fileSystem.CombinePath(_cacheRoot, repoHash, shortSha);
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
