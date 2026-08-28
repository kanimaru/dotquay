using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Dotnet;

/// <summary>
/// Searches several NuGet feeds in declared order and answers with the first that has the package
/// (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// <para>Implemented as an <see cref="INuGetClient"/> rather than as logic inside
/// <see cref="Workflows.DependencyWorkflow"/> so the workflow keeps asking one question of one
/// object, and the feed-ordering rules live in one testable place.</para>
/// <para>First-match, not highest-version-across-feeds. Taking the highest version wherever it lives
/// sounds more helpful and is a dependency-confusion substitution attack: publish
/// <c>Kani.Core 99.0.0</c> to a public feed and every project that resolves a private
/// <c>Kani.Core</c> across both silently switches to it.</para>
/// </remarks>
public sealed class MultiSourceNuGetClient : INuGetClient
{
    private readonly IReadOnlyList<(NuGetSourceSpec Source, INuGetClient Client)> _clients;
    private readonly List<string> _warnings = [];

    public MultiSourceNuGetClient(IReadOnlyList<(NuGetSourceSpec Source, INuGetClient Client)> clients)
    {
        _clients = clients;
    }

    /// <summary>Non-fatal notes accumulated while querying — currently "this package exists on more
    /// than one of your feeds". Surfaced through the envelope's <c>warnings</c> (SPEC.md §17) rather
    /// than printed, so a GUI caller gets them too.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public async Task<NuGetVersionQuery> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default)
    {
        NuGetVersionQuery? winner = null;

        foreach (var (source, client) in _clients)
        {
            // A 401 propagates out of here on purpose: NuGetV3Client throws
            // NuGetAuthenticationException rather than returning empty, so a feed that *might* have
            // the package and won't say stops the search instead of quietly deferring to a public
            // feed further down the list. NuGetSourceException propagates for the same reason — a
            // feed that is misconfigured, unreachable, or invisible to the current credential is a
            // problem to report, not a reason to silently resolve from somewhere else. That matters
            // most on GitLab, which answers 404 rather than 401 for a project you can't see.
            var query = await client.GetVersionsAsync(packageId, cancellationToken);
            if (!query.Found)
            {
                continue;
            }

            if (winner is null)
            {
                winner = query;
                continue;
            }

            // Found again on a later feed. Not an error — a private feed legitimately mirrors public
            // packages — but worth saying, because the two copies can differ and only the first is
            // ever used.
            _warnings.Add(
                $"'{packageId}' is available on more than one NuGet source ("
                + $"{winner.SourceName ?? NuGetSourceSpec.PublicName} and {source.Name}). "
                + $"Using {winner.SourceName ?? NuGetSourceSpec.PublicName}; pin it with "
                + $"source = \"...\" under [dotnet.{packageId}] to be explicit.");

            break;
        }

        return winner ?? NuGetVersionQuery.Empty;
    }

    public async Task<bool> PackageExistsAsync(string packageId, CancellationToken cancellationToken = default) =>
        (await GetVersionsAsync(packageId, cancellationToken)).Found;
}
