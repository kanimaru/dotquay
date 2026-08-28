using Dotquay.Core.Abstractions;
using NuGet.Versioning;

namespace Dotquay.Core.Dotnet;

/// <summary>Picks a concrete version out of what <see cref="INuGetClient.GetVersionsAsync"/>
/// returns. Dotquay pins dotnet dependencies to an exact resolved version at <c>add</c>/<c>update</c>
/// time — same "manifest records what you asked for, lockfile records what you got" split as git
/// addons, just without a range-matching step, since NuGet.Versioning's range grammar is NuGet's own
/// job to interpret at restore time, not dotquay's to reimplement (SPEC.md §11).</summary>
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

    public static async Task<string?> SelectLatestStableAsync(
        INuGetClient client, string packageId, CancellationToken cancellationToken = default) =>
        SelectLatestStable((await client.GetVersionsAsync(packageId, cancellationToken)).Versions);

    /// <summary>Confirms an explicitly-requested version actually exists for the package (compared as
    /// parsed versions, not strings) — <c>add &lt;id&gt; --version</c> shouldn't silently lock a
    /// typo.</summary>
    public static bool Exists(IReadOnlyList<string> versions, string version) =>
        NuGetVersion.TryParse(version, out var requested)
        && versions.Any(v => NuGetVersion.TryParse(v, out var parsed) && parsed == requested);

    public static async Task<bool> ExistsAsync(
        INuGetClient client, string packageId, string version, CancellationToken cancellationToken = default) =>
        Exists((await client.GetVersionsAsync(packageId, cancellationToken)).Versions, version);

    /// <summary>Backs <c>dotquay outdated</c>'s NuGet check (SPEC.md §6, requirement P1-3): returns
    /// the latest stable version if it's strictly newer than <paramref name="currentVersion"/>, else
    /// null (already current, or the package has no stable release at all). Uses
    /// <see cref="NuGetVersion"/> comparison rather than string equality so normalization
    /// differences (e.g. <c>"13.0.3"</c> vs. <c>"13.0.3.0"</c>) don't falsely report drift.</summary>
    public static async Task<string?> CheckForNewerStableAsync(
        INuGetClient client, string packageId, string currentVersion, CancellationToken cancellationToken = default)
    {
        var latest = await SelectLatestStableAsync(client, packageId, cancellationToken);
        if (latest is null)
        {
            return null;
        }

        if (!NuGetVersion.TryParse(latest, out var latestVersion) || !NuGetVersion.TryParse(currentVersion, out var current))
        {
            return null;
        }

        return latestVersion > current ? latest : null;
    }
}
