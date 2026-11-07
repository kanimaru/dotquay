using System.Globalization;

namespace Dotquay.Core.Versioning;

/// <summary>
/// A lenient parse of a git tag into a comparable version, per SPEC.md §15's tag-habit survey
/// (OQ-3 findings): strip a leading "v", accept 2-4 numeric components, tolerate a "-suffix".
/// "-dev"/"-rc"-style suffixes sort below the plain release; "-stable" counts as the release
/// itself. This is deliberately not full semver — real-world Godot addon tags don't follow it
/// (Phantom Camera ships 4-component tags, godot-sqlite ships 2-component ones).
/// </summary>
/// <remarks>
/// Also the version type for NuGet packages (DESIGN-v0.21 §5): one type means one ordering, so a
/// <see cref="VersionConstraint"/> reads the same against a git tag and a feed's version list.
/// Build metadata (<c>+sha</c>) is ignored, as SemVer 2.0 requires.
/// </remarks>
public sealed class AddonVersion : IComparable<AddonVersion> {
    private AddonVersion(string originalTag, IReadOnlyList<long> components, string? prereleaseLabel) {
        OriginalTag = originalTag;
        Components = components;
        PrereleaseLabel = prereleaseLabel;
    }

    /// <summary>The tag exactly as it appeared in git, e.g. <c>"v2.44.0-rc1"</c>.</summary>
    public string OriginalTag { get; }

    public IReadOnlyList<long> Components { get; }

    /// <summary>Null for a plain release or a <c>-stable</c> suffix; otherwise the raw suffix text
    /// (e.g. <c>"dev1"</c>, <c>"rc1"</c>).</summary>
    public string? PrereleaseLabel { get; }

    public bool IsPrerelease => PrereleaseLabel is not null;

    public static bool TryParse(string tag, out AddonVersion? version) {
        version = null;

        if (string.IsNullOrWhiteSpace(tag)) {
            return false;
        }

        var text = tag.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) {
            text = text[1..];
        }

        var plusIndex = text.IndexOf('+');
        if (plusIndex >= 0) {
            text = text[..plusIndex];
        }

        var numericPart = text;
        string? suffix = null;
        var dashIndex = text.IndexOf('-');
        if (dashIndex >= 0) {
            numericPart = text[..dashIndex];
            suffix = text[(dashIndex + 1)..];
        }

        var pieces = numericPart.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length is < 2 or > 4) {
            return false;
        }

        var components = new List<long>(pieces.Length);
        foreach (var piece in pieces) {
            if (!long.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) {
                return false;
            }

            components.Add(value);
        }

        var prereleaseLabel = string.IsNullOrEmpty(suffix) || string.Equals(suffix, "stable", StringComparison.OrdinalIgnoreCase)
            ? null
            : suffix;

        version = new AddonVersion(tag, components, prereleaseLabel);
        return true;
    }

    public int CompareTo(AddonVersion? other) {
        if (other is null) {
            return 1;
        }

        var cmp = CompareComponents(Components, other.Components);
        if (cmp != 0) {
            return cmp;
        }

        return ComparePrerelease(PrereleaseLabel, other.PrereleaseLabel);
    }

    /// <summary>
    /// SemVer 2.0 precedence for two prerelease labels, null meaning a release: a release sorts above
    /// any prerelease of the same components.
    /// </summary>
    /// <remarks>
    /// Identifiers are dot-separated. Numeric ones compare as numbers, so <c>rc.2 &lt; rc.10</c>;
    /// alphanumeric ones compare ordinally and sort above numeric ones; a shorter list sorts first when
    /// every shared identifier is equal. An ordinal compare of the whole label got <c>rc.10</c> below
    /// <c>rc.2</c>.
    /// </remarks>
    internal static int ComparePrerelease(string? a, string? b) {
        if (a is null || b is null) {
            return a is null ? (b is null ? 0 : 1) : -1;
        }

        var left = a.Split('.');
        var right = b.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++) {
            var leftNumeric = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var l);
            var rightNumeric = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var r);

            var cmp = (leftNumeric, rightNumeric) switch {
                (true, true) => l.CompareTo(r),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i]),
            };

            if (cmp != 0) {
                return Math.Sign(cmp);
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    internal static int CompareComponents(IReadOnlyList<long> a, IReadOnlyList<long> b) {
        var length = Math.Max(a.Count, b.Count);
        for (var i = 0; i < length; i++) {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            var cmp = x.CompareTo(y);
            if (cmp != 0) {
                return cmp;
            }
        }

        return 0;
    }

    public override string ToString() => OriginalTag;
}
