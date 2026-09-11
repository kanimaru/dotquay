using Dotquay.Core.Abstractions;

namespace Dotquay.Tests.Support;

/// <summary>Configurable <see cref="INuGetClient"/> test double — no real network call needed to
/// test NuGet version selection (mirrors <see cref="FakeGitClient"/>'s role for the git side).</summary>
public sealed class FakeNuGetClient : INuGetClient
{
    public FakeNuGetClient(string? sourceName = null)
    {
        SourceName = sourceName;
    }

    /// <summary>The feed name this fake reports as having answered — what a real client puts in
    /// <see cref="NuGetVersionQuery.SourceName"/> and what ends up in the lockfile. Null means
    /// nuget.org.</summary>
    public string? SourceName { get; set; }

    public Dictionary<string, IReadOnlyList<string>> VersionsByPackageId { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Package IDs this fake answers with an authentication failure instead of a version
    /// list — for testing that a 401 stops an ordered multi-feed search rather than falling through
    /// to a public feed.</summary>
    public HashSet<string> UnauthorizedPackageIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeNuGetClient With(string packageId, params string[] versions)
    {
        VersionsByPackageId[packageId] = versions;
        return this;
    }

    public Task<NuGetVersionQuery> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default)
    {
        if (UnauthorizedPackageIds.Contains(packageId))
        {
            throw new Dotquay.Core.Dotnet.NuGetAuthenticationException(
                SourceName ?? "nuget.org", "https://example.invalid/index.json", $"401 for '{packageId}'.");
        }

        return Task.FromResult(new NuGetVersionQuery(
            VersionsByPackageId.TryGetValue(packageId, out var versions) ? versions : [], SourceName));
    }

    public async Task<bool> PackageExistsAsync(string packageId, CancellationToken cancellationToken = default) =>
        (await GetVersionsAsync(packageId, cancellationToken)).Found;

    /// <summary>Set to make <see cref="VerifySourceAsync"/> fail the way a wrong URL or a rejected
    /// credential does — what <c>source add</c>'s verify-before-write path is tested against.</summary>
    public Exception? VerifyFailure { get; set; }

    /// <summary>Counts verification calls, so a test can assert <c>--no-verify</c> really skipped the
    /// network rather than merely tolerating a reachable feed.</summary>
    public int VerifyCount { get; private set; }

    /// <summary>Package IDs this fake reports as author-declared development dependencies — what
    /// drives the generator question on <c>add</c>.</summary>
    public HashSet<string> DevelopmentDependencyPackageIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Package IDs whose nuspec this fake cannot serve at all, so the shape is unknown rather
    /// than a positive "not a generator". The distinction is the whole contract.</summary>
    public HashSet<string> UnreadableNuspecPackageIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<PackageShape> GetPackageShapeAsync(
        string packageId, string version, CancellationToken cancellationToken = default) =>
        Task.FromResult(UnreadableNuspecPackageIds.Contains(packageId)
            ? PackageShape.Unknown
            : new PackageShape(DevelopmentDependencyPackageIds.Contains(packageId)));

    public Task VerifySourceAsync(CancellationToken cancellationToken = default)
    {
        VerifyCount++;

        return VerifyFailure is null ? Task.CompletedTask : Task.FromException(VerifyFailure);
    }
}
