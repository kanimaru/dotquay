using System.Globalization;

namespace Dotquay.Core.Versioning;

/// <summary>
/// Parses and matches the <c>version = "~2.44"</c> addon-spec field (SPEC.md §7.1) against
/// resolved tags. Supports Cargo-style tilde ranges (<c>~I.J.K</c>, <c>~I.J</c>, <c>~I</c>) and
/// exact versions. Prerelease tags are excluded from matches — "version" resolution never
/// silently picks a "-dev"/"-rc" build (SPEC.md §15).
/// </summary>
public sealed class VersionConstraint
{
    private readonly IReadOnlyList<long> _components;
    private readonly bool _isExact;

    private VersionConstraint(IReadOnlyList<long> components, bool isExact)
    {
        _components = components;
        _isExact = isExact;
    }

    public static bool TryParse(string constraint, out VersionConstraint? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(constraint))
        {
            return false;
        }

        var text = constraint.Trim();
        var isExact = true;
        if (text.StartsWith('~'))
        {
            isExact = false;
            text = text[1..];
        }

        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        // Deliberately not AddonVersion.TryParse here: a constraint's own version text is allowed
        // a single component ("~1"), which AddonVersion rejects for real tags (SPEC.md §15 requires
        // 2-4 components there, since bare single-number git tags aren't a thing in the wild).
        var pieces = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length is < 1 or > 4)
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

        parsed = new VersionConstraint(components, isExact);
        return true;
    }

    public bool IsSatisfiedBy(AddonVersion candidate)
    {
        if (candidate.IsPrerelease)
        {
            return false;
        }

        if (_isExact)
        {
            return AddonVersion.CompareComponents(candidate.Components, _components) == 0;
        }

        if (AddonVersion.CompareComponents(candidate.Components, _components) < 0)
        {
            return false;
        }

        // Cargo-style tilde upper bound: ~I -> <(I+1); ~I.J(.*) -> <I.(J+1) (bump the minor
        // position and ignore anything finer, same as Cargo does for ~I.J.K bumping minor not
        // patch — a 4th "build"-style component our lenient parser accepts is treated the same
        // way as a patch component here).
        var upperBound = _components.Count == 1
            ? new List<long> { _components[0] + 1 }
            : new List<long> { _components[0], _components[1] + 1 };

        return AddonVersion.CompareComponents(candidate.Components, upperBound) < 0;
    }
}
