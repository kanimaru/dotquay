using System.Globalization;

namespace Dotquay.Core.Versioning;

/// <summary>
/// A lenient parse of a git tag into a comparable version, per SPEC.md §15's tag-habit survey
/// (OQ-3 findings): strip a leading "v", accept 2-4 numeric components, tolerate a "-suffix".
/// "-dev"/"-rc"-style suffixes sort below the plain release; "-stable" counts as the release
/// itself. This is deliberately not full semver — real-world Godot addon tags don't follow it
/// (Phantom Camera ships 4-component tags, godot-sqlite ships 2-component ones).
/// </summary>
public sealed class AddonVersion : IComparable<AddonVersion>
{
    private AddonVersion(string originalTag, IReadOnlyList<long> components, string? prereleaseLabel)
    {
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

    public static bool TryParse(string tag, out AddonVersion? version)
    {
        version = null;

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        var numericPart = text;
        string? suffix = null;
        var dashIndex = text.IndexOf('-');
        if (dashIndex >= 0)
        {
            numericPart = text[..dashIndex];
            suffix = text[(dashIndex + 1)..];
        }

        var pieces = numericPart.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length is < 2 or > 4)
        {
            return false;
        }

        var components = new List<long>(pieces.Length);
        foreach (var piece in pieces)
        {
            if (!long.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
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

    public int CompareTo(AddonVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var cmp = CompareComponents(Components, other.Components);
        if (cmp != 0)
        {
            return cmp;
        }

        if (IsPrerelease && !other.IsPrerelease)
        {
            return -1;
        }

        if (!IsPrerelease && other.IsPrerelease)
        {
            return 1;
        }

        return IsPrerelease && other.IsPrerelease
            ? string.CompareOrdinal(PrereleaseLabel, other.PrereleaseLabel)
            : 0;
    }

    internal static int CompareComponents(IReadOnlyList<long> a, IReadOnlyList<long> b)
    {
        var length = Math.Max(a.Count, b.Count);
        for (var i = 0; i < length; i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            var cmp = x.CompareTo(y);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }

    public override string ToString() => OriginalTag;
}
