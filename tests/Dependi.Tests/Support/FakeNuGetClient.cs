using Dependi.Core.Abstractions;

namespace Dependi.Tests.Support;

/// <summary>Configurable <see cref="INuGetClient"/> test double — no real network call needed to
/// test NuGet version selection (mirrors <see cref="FakeGitClient"/>'s role for the git side).</summary>
public sealed class FakeNuGetClient : INuGetClient
{
    public Dictionary<string, IReadOnlyList<string>> VersionsByPackageId { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default) =>
        Task.FromResult(VersionsByPackageId.TryGetValue(packageId, out var versions) ? versions : []);

    public async Task<bool> PackageExistsAsync(string packageId, CancellationToken cancellationToken = default) =>
        (await GetVersionsAsync(packageId, cancellationToken)).Count > 0;
}
