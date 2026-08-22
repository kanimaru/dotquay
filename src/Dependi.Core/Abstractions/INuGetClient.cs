using System.Threading;

namespace Dependi.Core.Abstractions;

/// <summary>
/// Reads NuGet metadata via the official <c>NuGet.Protocol</c> client library (SPEC.md §11).
/// Dependi never reimplements NuGet resolution or caching — this interface only covers the
/// read-side queries <c>add</c>/<c>outdated</c>/<c>list</c> need; writing <c>PackageReference</c>
/// entries and running <c>dotnet restore</c> stay separate, explicit steps in the sync engine.
/// </summary>
public interface INuGetClient
{
    /// <summary>Lists known versions for a package ID, newest first.</summary>
    Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default);

    Task<bool> PackageExistsAsync(string packageId, CancellationToken cancellationToken = default);
}
