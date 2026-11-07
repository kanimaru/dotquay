using Dotquay.Core.Versioning;

namespace Dotquay.Core.SelfUpdate;

/// <summary>
/// Ordering for dotquay's own version strings.
/// </summary>
/// <remarks>
/// Reuses <see cref="AddonVersion"/> rather than adding a second version parser to this codebase.
/// It already handles the shapes that actually occur here: a leading <c>v</c>, two-, three- and
/// four-component numbers, and a prerelease suffix — and dotquay's CI stamps
/// <c>0.0.0-ci-&lt;sha&gt;</c> onto merge-request builds, which must sort below every real release
/// so a developer running one is told about the release rather than the other way round.
/// </remarks>
public static class VersionOrder {
    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    /// <summary>Negative when <paramref name="a"/> is older. Unparseable versions fall back to an
    /// ordinal comparison, which is arbitrary but total — the alternative is throwing inside a
    /// background update check, and a notice must never be able to fail a command.</summary>
    public static int Compare(string? a, string? b) {
        if (string.IsNullOrWhiteSpace(a)) {
            return string.IsNullOrWhiteSpace(b) ? 0 : -1;
        }

        if (string.IsNullOrWhiteSpace(b)) {
            return 1;
        }

        return AddonVersion.TryParse(a, out var left) && AddonVersion.TryParse(b, out var right)
            ? left!.CompareTo(right!)
            : string.CompareOrdinal(a, b);
    }
}
