using Dotquay.Core.Manifest;

namespace Dotquay.Core.Auth;

/// <summary>
/// The URLs a project already trusts on a host, for <c>dotquay login &lt;host&gt;</c> to check a
/// new credential against (SPEC.md §11.2).
/// </summary>
/// <remarks>
/// <para>A bare host has no endpoint of its own worth probing: GitLab's generic API answers HTTP
/// Basic with 401 even for a valid token (measured against gitlab.kemomi.de, 2026-10-21:
/// <c>/api/v4/user</c> and <c>/api/v4/version</c> both 401 with a token that the package registry
/// accepts), and a guessed well-known path fails confidently against servers that simply don't have
/// it. The project's own manifest, on the other hand, names exactly the URLs the credential is for
/// — so those are what get probed, and a login outside any project honestly says it couldn't
/// check.</para>
/// <para>Order matters a little: NuGet feeds first, because that is what the token is most often
/// for, then git remotes. The verifier stops at the first decisive answer.</para>
/// </remarks>
public static class CredentialProbeTargets {
    /// <summary>What git's smart HTTP protocol fetches first; a host that wants credentials answers
    /// it with 401, which is all the probe needs.</summary>
    public const string GitInfoRefsSuffix = "/info/refs?service=git-upload-pack";

    /// <summary>Every manifest URL that <paramref name="scope"/> covers, as probe URLs: NuGet
    /// service indexes as they are, git remotes as their <c>info/refs</c> endpoint. ssh remotes and
    /// the public nuget.org feed contribute nothing — the former can't carry this credential, the
    /// latter never needs one.</summary>
    public static IReadOnlyList<string> For(CredentialScope scope, ProjectManifest manifest) {
        var targets = new List<string>();

        foreach (var source in manifest.NuGetSources) {
            if (!source.IsPublicDefault && IsCovered(scope, source.Url)) {
                targets.Add(source.Url);
            }
        }

        foreach (var addon in manifest.Addons.Values) {
            if (IsCovered(scope, addon.Git)) {
                targets.Add(GitInfoRefsUrl(addon.Git));
            }
        }

        return targets.Distinct(StringComparer.Ordinal).ToList();
    }

    public static string GitInfoRefsUrl(string remote) => remote.TrimEnd('/') + GitInfoRefsSuffix;

    private static bool IsCovered(CredentialScope scope, string url) =>
        url.Contains("://", StringComparison.Ordinal)
        && CredentialScope.TryParse(url, out var target, out _)
        && scope.Covers(target);
}
