using Dependi.Core.Abstractions;
using NuGet.Versioning;

namespace Dependi.Core.Dotnet;

/// <summary>Picks a concrete version out of what <see cref="INuGetClient.GetVersionsAsync"/>
/// returns. Dependi pins dotnet dependencies to an exact resolved version at <c>add</c>/<c>update</c>
/// time — same "manifest records what you asked for, lockfile records what you got" split as git
/// addons, just without a range-matching step, since NuGet.Versioning's range grammar is NuGet's own
/// job to interpret at restore time, not dependi's to reimplement (SPEC.md §11).</summary>
public static class NuGetVersionSelector
{
    /// <summary>The newest non-prerelease version, or null if the package has no stable release at
    /// all (a package with only prerelease versions needs an explicit version passed to <c>add</c>).</summary>
    public static async Task<string?> SelectLatestStableAsync(
        INuGetClient client, string packageId, CancellationToken cancellationToken = default)
    {
        var versions = await client.GetVersionsAsync(packageId, cancellationToken);

        return versions
            .Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null)
            .Where(v => v is not null && !v.IsPrerelease)
            .OrderByDescending(v => v)
            .FirstOrDefault()
            ?.ToNormalizedString();
    }

    /// <summary>Confirms an explicitly-requested version actually exists for the package (exact
    /// string match against nuget.org's normalized versions) — <c>add &lt;id&gt; --version</c>
    /// shouldn't silently lock a typo.</summary>
    public static async Task<bool> ExistsAsync(
        INuGetClient client, string packageId, string version, CancellationToken cancellationToken = default)
    {
        if (!NuGetVersion.TryParse(version, out var requested))
        {
            return false;
        }

        var versions = await client.GetVersionsAsync(packageId, cancellationToken);
        return versions.Any(v => NuGetVersion.TryParse(v, out var parsed) && parsed == requested);
    }

    /// <summary>Backs <c>dependi outdated</c>'s NuGet check (SPEC.md §6, requirement P1-3): returns
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
