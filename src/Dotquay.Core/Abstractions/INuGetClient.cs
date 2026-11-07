using System.Threading;

namespace Dotquay.Core.Abstractions;

/// <summary>
/// Reads NuGet metadata over the V3 HTTP protocol (SPEC.md §11). Dotquay never reimplements NuGet
/// resolution or caching — this interface only covers the read-side queries
/// <c>add</c>/<c>outdated</c>/<c>list</c> need; writing <c>PackageReference</c> entries and running
/// <c>dotnet restore</c> stay separate, explicit steps in the sync engine.
/// </summary>
public interface INuGetClient {
    /// <summary>Lists known versions for a package ID, and names the feed they came from.</summary>
    Task<NuGetVersionQuery> GetVersionsAsync(string packageId,
        CancellationToken cancellationToken = default);

    Task<bool> PackageExistsAsync(string packageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reaches the source's V3 service index and throws if it isn't a usable feed.
    /// </summary>
    /// <remarks>
    /// <para>Its own operation rather than "look up a package nobody has published and see whether
    /// it throws", because the two failures being distinguished are different: a package lookup
    /// answers 404 for a package that simply isn't there, which is a perfectly healthy feed. Only
    /// the service index can say whether the URL is a feed at all.</para>
    /// <para>Exists for <c>source add</c>, which verifies before writing to the manifest (SPEC.md
    /// §11.1 already makes an unanswerable service index fatal at resolve time — this applies the
    /// same rule at declaration time). Throws <see cref="Dotnet.NuGetSourceException"/> or
    /// <see cref="Dotnet.NuGetAuthenticationException"/>; returning a bool would collapse "wrong
    /// URL", "bad token" and "no token" into one answer that can't be acted on.</para>
    /// </remarks>
    Task VerifySourceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads what a package says about itself from its <c>.nuspec</c> (v0.14).
    /// </summary>
    /// <remarks>
    /// One extra request against the <c>PackageBaseAddress</c> resource already discovered for
    /// version queries, and no package download — which is the whole reason the signal read is the
    /// author's <c>developmentDependency</c> flag rather than the package's folder layout. Returns
    /// <see cref="PackageShape.Unknown"/> rather than throwing when the document is missing or
    /// unreadable: this drives an optional question, and a feed that will not serve a nuspec must
    /// not be able to fail an <c>add</c> that would otherwise have worked.
    /// </remarks>
    Task<PackageShape> GetPackageShapeAsync(
        string packageId, string version, CancellationToken cancellationToken = default);
}

/// <summary>
/// The answer to a version query: the versions, and which declared source produced them.
/// </summary>
/// <remarks>
/// <para><see cref="SourceName"/> is why this is a record and not the bare
/// <c>IReadOnlyList&lt;string&gt;</c> it replaced. With several feeds searched in order (SPEC.md
/// §11.1), "which one answered" has to reach the lockfile, and the only alternative routes were a
/// downcast in the workflow or a mutable property on the aggregating client — both of which make
/// the answer depend on when you ask rather than on what was returned.</para>
/// <para>Null <see cref="SourceName"/> means nuget.org, matching the lockfile's convention of
/// omitting the key for the public feed.</para>
/// </remarks>
/// <param name="Versions">Whatever order the feed returned — callers that care sort
/// explicitly.</param>
/// <param name="SourceName">The declared source name, or null for the public default.</param>
public sealed record NuGetVersionQuery(IReadOnlyList<string> Versions, string? SourceName) {
    public static readonly NuGetVersionQuery Empty = new([], null);

    public bool Found => Versions.Count > 0;

    /// <summary>Every source name <see cref="Dotnet.MultiSourceNuGetClient"/> actually queried
    /// before giving up, in declared order — empty unless nothing was found. Lets "no stable
    /// release" name which feeds came back empty instead of reading as "you never declared a source
    /// for this", which is exactly the wrong read when a private feed was consulted and 404'd
    /// (SPEC.md §11.1's ambiguity: GitLab answers 404 both for a package that was never published
    /// and one the token can't see).</summary>
    public IReadOnlyList<string> CheckedSources { get; init; } = [];
}
