using System.Threading;

namespace Dotquay.Core.Abstractions;

/// <summary>
/// Reads NuGet metadata over the V3 HTTP protocol (SPEC.md §11). Dotquay never reimplements NuGet
/// resolution or caching — this interface only covers the read-side queries
/// <c>add</c>/<c>outdated</c>/<c>list</c> need; writing <c>PackageReference</c> entries and running
/// <c>dotnet restore</c> stay separate, explicit steps in the sync engine.
/// </summary>
public interface INuGetClient
{
    /// <summary>Lists known versions for a package ID, and names the feed they came from.</summary>
    Task<NuGetVersionQuery> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default);

    Task<bool> PackageExistsAsync(string packageId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The answer to a version query: the versions, and which declared source produced them.
/// </summary>
/// <remarks>
/// <para><see cref="SourceName"/> is why this is a record and not the bare
/// <c>IReadOnlyList&lt;string&gt;</c> it replaced. With several feeds searched in order (SPEC.md
/// §11.1), "which one answered" has to reach the lockfile, and the only alternative routes were a
/// downcast in the workflow or a mutable property on the aggregating client — both of which make the
/// answer depend on when you ask rather than on what was returned.</para>
/// <para>Null <see cref="SourceName"/> means nuget.org, matching the lockfile's convention of
/// omitting the key for the public feed.</para>
/// </remarks>
/// <param name="Versions">Whatever order the feed returned — callers that care sort explicitly.</param>
/// <param name="SourceName">The declared source name, or null for the public default.</param>
public sealed record NuGetVersionQuery(IReadOnlyList<string> Versions, string? SourceName)
{
    public static readonly NuGetVersionQuery Empty = new([], null);

    public bool Found => Versions.Count > 0;
}
