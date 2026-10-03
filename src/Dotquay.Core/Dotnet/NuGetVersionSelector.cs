using Dotquay.Core.Abstractions;
using Dotquay.Core.Versioning;
using NuGet.Versioning;

namespace Dotquay.Core.Dotnet;

/// <summary>Picks a concrete version out of what <see cref="INuGetClient.GetVersionsAsync"/>
/// returns. Dotquay pins dotnet dependencies to an exact resolved version at <c>add</c>/<c>update</c>
/// time — same "manifest records what you asked for, lockfile records what you got" split as git
/// addons, with the same range language (<see cref="VersionConstraint"/>, DESIGN-v0.21). NuGet's
/// own range syntax never applies: the <c>.csproj</c> only ever receives the exact result.</summary>
/// <remarks>
/// The selection rules are pure functions over an already-fetched version list, with thin async
/// wrappers over them. Callers that also need to know <em>which feed</em> answered (SPEC.md §7.3's
/// lockfile <c>source</c>) query once and pass the list in, rather than querying twice and hoping
/// both calls hit the same feed.
/// </remarks>
public static class NuGetVersionSelector
{
    /// <summary>The newest non-prerelease version, or null if the package has no stable release at
    /// all (a package with only prerelease versions needs an explicit version passed to <c>add</c>).</summary>
    public static string? SelectLatestStable(IReadOnlyList<string> versions) =>
        versions
            .Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null)
            .Where(v => v is not null && !v.IsPrerelease)
            .OrderByDescending(v => v)
            .FirstOrDefault()
            ?.ToNormalizedString();

    /// <summary>
    /// The highest version satisfying <paramref name="constraint"/>, as the feed spelled it, or null
    /// when none does (DESIGN-v0.21 §3.5). Versions the tag parser can't read are skipped, the same as
    /// unreadable tags on the git side.
    /// </summary>
    public static string? SelectHighestMatching(IReadOnlyList<string> versions, VersionConstraint constraint) =>
        versions
            .Select(v => AddonVersion.TryParse(v, out var parsed) ? parsed : null)
            .Where(v => v is not null && constraint.IsSatisfiedBy(v))
            .OrderByDescending(v => v)
            .FirstOrDefault()
            ?.OriginalTag;

    public static async Task<string?> SelectLatestStableAsync(
        INuGetClient client, string packageId, CancellationToken cancellationToken = default) =>
        SelectLatestStable((await client.GetVersionsAsync(packageId, cancellationToken)).Versions);

    /// <summary>Backs <c>dotquay outdated</c>'s NuGet check (SPEC.md §6, requirement P1-3): returns
    /// the latest stable version if it's strictly newer than <paramref name="currentVersion"/>, else
    /// null (already current, or the package has no stable release at all). Uses
    /// <see cref="NuGetVersion"/> comparison rather than string equality so normalization
    /// differences (e.g. <c>"13.0.3"</c> vs. <c>"13.0.3.0"</c>) don't falsely report drift.</summary>
    public static async Task<string?> CheckForNewerStableAsync(
        INuGetClient client, string packageId, string currentVersion, CancellationToken cancellationToken = default) =>
        await CheckForNewerWithinAsync(client, packageId, currentVersion, constraint: null, cancellationToken);

    /// <summary>
    /// <see cref="CheckForNewerStableAsync"/> for a package whose manifest names a range: the highest
    /// version <em>inside that range</em> if it is strictly newer than <paramref name="currentVersion"/>,
    /// else null. This is the version <c>update</c> would pick (DESIGN-v0.21 §3.5), so <c>outdated</c>
    /// never reports an upgrade <c>update</c> would refuse to make: a package pinned to <c>3.0.0</c> is
    /// current while 3.1.1 exists. With no constraint it is the newest stable release, as before.
    /// </summary>
    public static async Task<string?> CheckForNewerWithinAsync(
        INuGetClient client, string packageId, string currentVersion, VersionConstraint? constraint,
        CancellationToken cancellationToken = default)
    {
        var versions = (await client.GetVersionsAsync(packageId, cancellationToken)).Versions;
        var best = constraint is null ? SelectLatestStable(versions) : SelectHighestMatching(versions, constraint);
        if (best is null)
        {
            return null;
        }

        if (!NuGetVersion.TryParse(best, out var bestVersion) || !NuGetVersion.TryParse(currentVersion, out var current))
        {
            return null;
        }

        return bestVersion > current ? best : null;
    }
}
