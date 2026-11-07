using System.Globalization;

namespace Dotquay.Core.Versioning;

/// <summary>
/// A <c>version = "..."</c> constraint, for every <c>[addons.*]</c> and <c>[dotnet.*]</c> entry in
/// every dotquay manifest (DESIGN-v0.21). The grammar is node-semver's: a bare version is exact,
/// <c>^</c> allows compatible updates, <c>~</c> patch-level ones, <c>1.2.x</c>/<c>*</c> are
/// x-ranges, and comparators, hyphen ranges and <c>||</c> cover the rest.
/// </summary>
/// <remarks>
/// <para>Two dotquay extensions, both from DESIGN-v0.21 §3.2: a fourth component (Phantom Camera's
/// <c>v0.11.0.3</c>, NuGet's <c>Revision</c>) that is compared like the others but that <c>~</c>
/// and <c>^</c> never bump, and a comma accepted as a synonym for the space between
/// comparators.</para>
/// <para>Prereleases follow npm's rule (§3.4): one satisfies a range only when some comparator of
/// that range names a prerelease of the same version numbers, so <c>^5.0</c> never selects
/// <c>5.1.0-rc1</c>, while <c>^5.1.0-rc1</c> accepts <c>5.1.0-rc5</c>.</para>
/// </remarks>
public sealed class VersionConstraint {
    // A bare version with at least this many components is exact; fewer make it an x-range.
    private const int FullComponentCount = 3;

    private readonly IReadOnlyList<IReadOnlyList<Comparator>> _alternatives;

    private VersionConstraint(string text, IReadOnlyList<IReadOnlyList<Comparator>> alternatives) {
        Text = text;
        _alternatives = alternatives;
    }

    /// <summary>The constraint as written.</summary>
    public string Text { get; }

    public static bool TryParse(string constraint, out VersionConstraint? parsed) {
        parsed = null;
        if (constraint is null) {
            return false;
        }

        var alternatives = new List<IReadOnlyList<Comparator>>();
        foreach (var alternative in constraint.Split("||")) {
            if (!TryParseRange(alternative, out var comparators)) {
                return false;
            }

            alternatives.Add(comparators);
        }

        parsed = new VersionConstraint(constraint.Trim(), alternatives);
        return true;
    }

    public bool IsSatisfiedBy(AddonVersion candidate) =>
        _alternatives.Any(range => Satisfies(range, candidate));

    /// <summary>
    /// A constraint satisfied exactly by the versions that satisfy both — how several requirers'
    /// ranges on one package combine (DESIGN-v0.21 §3.5).
    /// </summary>
    public VersionConstraint Intersect(VersionConstraint other) {
        var alternatives = new List<IReadOnlyList<Comparator>>();
        foreach (var left in _alternatives) {
            foreach (var right in other._alternatives) {
                alternatives.Add([.. left, .. right]);
            }
        }

        return new VersionConstraint($"{Text} and {other.Text}", alternatives);
    }

    public override string ToString() => Text;

    private static bool Satisfies(IReadOnlyList<Comparator> range, AddonVersion candidate) {
        if (!range.All(comparator => comparator.IsSatisfiedBy(candidate))) {
            return false;
        }

        return !candidate.IsPrerelease
            || range.Any(comparator => comparator.Prerelease is not null
                && AddonVersion.CompareComponents(comparator.Components,
                    candidate.Components) == 0);
    }

    private static bool TryParseRange(string text, out IReadOnlyList<Comparator> comparators) {
        comparators = [];
        var tokens = Tokenize(text);

        if (tokens.Count == 3 && tokens[1] == "-") {
            return TryParseHyphen(tokens[0], tokens[2], out comparators);
        }

        var result = new List<Comparator>();
        foreach (var token in tokens) {
            if (!TryParseComparator(token, result)) {
                return false;
            }
        }

        comparators = result;
        return true;
    }

    /// <summary>
    /// Splits on spaces and commas, gluing an operator written apart from its version
    /// (<c>&gt;= 1.2</c>) back onto it.
    /// </summary>
    private static List<string> Tokenize(string text) {
        var raw = text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        var tokens = new List<string>();
        for (var i = 0; i < raw.Length; i++) {
            var token = raw[i];
            if (token is "<" or "<=" or ">" or ">=" or "=" or "~" or "^" && i + 1 < raw.Length) {
                token += raw[++i];
            }

            tokens.Add(token);
        }

        return tokens;
    }

    private static bool TryParseHyphen(string from, string to,
        out IReadOnlyList<Comparator> comparators) {
        comparators = [];
        if (!Partial.TryParse(from, out var lower) || !Partial.TryParse(to, out var upper)) {
            return false;
        }

        var result = new List<Comparator>();
        if (lower.Specified > 0) {
            result.Add(new Comparator(Operator.GreaterOrEqual, lower.Components, lower.Prerelease));
        }

        if (upper.Specified >= FullComponentCount) {
            result.Add(new Comparator(Operator.LessOrEqual, upper.Components, upper.Prerelease));
        } else if (upper.Specified > 0) {
            result.Add(new Comparator(Operator.Less, upper.Bump(upper.Specified - 1), null));
        }

        comparators = result;
        return true;
    }

    private static bool TryParseComparator(string token, List<Comparator> into) {
        var (prefix, rest) = token switch {
            _ when token.StartsWith(">=", StringComparison.Ordinal) => (">=", token[2..]),
            _ when token.StartsWith("<=", StringComparison.Ordinal) => ("<=", token[2..]),
            _ when token.StartsWith('>') => (">", token[1..]),
            _ when token.StartsWith('<') => ("<", token[1..]),
            _ when token.StartsWith('=') => ("=", token[1..]),
            _ when token.StartsWith('~') => ("~", token[1..]),
            _ when token.StartsWith('^') => ("^", token[1..]),
            _ => ("", token),
        };

        if (!Partial.TryParse(rest, out var version)) {
            return false;
        }

        var full = version.Specified >= FullComponentCount;
        var any = version.Specified == 0;

        switch (prefix) {
            case "" or "=":
                if (full) {
                    into.Add(new Comparator(Operator.Equal, version.Components,
                        version.Prerelease));
                } else if (!any) {
                    into.Add(new Comparator(Operator.GreaterOrEqual, version.Components, null));
                    into.Add(new Comparator(Operator.Less, version.Bump(version.Specified - 1),
                        null));
                }

                return true;

            case ">=":
                if (!any) {
                    into.Add(new Comparator(Operator.GreaterOrEqual, version.Components,
                        version.Prerelease));
                }

                return true;

            case ">":
                // ">1.2" means above every 1.2.x, as in npm; ">*" would match nothing and is
                // refused.
                if (any) {
                    return false;
                }

                into.Add(full
                    ? new Comparator(Operator.Greater, version.Components, version.Prerelease)
                    : new Comparator(Operator.GreaterOrEqual, version.Bump(version.Specified - 1),
                        null));
                return true;

            case "<":
                if (any) {
                    return false;
                }

                into.Add(new Comparator(Operator.Less, version.Components, version.Prerelease));
                return true;

            case "<=":
                if (full) {
                    into.Add(new Comparator(Operator.LessOrEqual, version.Components,
                        version.Prerelease));
                } else if (!any) {
                    into.Add(new Comparator(Operator.Less, version.Bump(version.Specified - 1),
                        null));
                }

                return true;

            case "~":
                if (!any) {
                    into.Add(new Comparator(Operator.GreaterOrEqual, version.Components,
                        version.Prerelease));
                    into.Add(new Comparator(Operator.Less,
                        version.Bump(Math.Min(version.Specified - 1, 1)), null));
                }

                return true;

            case "^":
                if (!any) {
                    into.Add(new Comparator(Operator.GreaterOrEqual, version.Components,
                        version.Prerelease));
                    into.Add(new Comparator(Operator.Less, version.Bump(CaretBumpIndex(version)),
                        null));
                }

                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The component <c>^</c> bumps: the leftmost non-zero of major, minor and patch, or the last
    /// one written when every written one is zero (<c>^0</c> → &lt;1, <c>^0.0</c> → &lt;0.1). Never
    /// the fourth.
    /// </summary>
    private static int CaretBumpIndex(Partial version) {
        var limit = Math.Min(version.Specified, FullComponentCount);
        for (var i = 0; i < limit; i++) {
            if (version.Components[i] != 0) {
                return i;
            }
        }

        return limit - 1;
    }

    private enum Operator {
        Equal,
        Greater,
        GreaterOrEqual,
        Less,
        LessOrEqual,
    }

    private sealed record Comparator(Operator Op, IReadOnlyList<long> Components,
        string? Prerelease) {
        public bool IsSatisfiedBy(AddonVersion candidate) {
            var cmp = AddonVersion.CompareComponents(candidate.Components, Components);
            if (cmp == 0) {
                cmp = AddonVersion.ComparePrerelease(candidate.PrereleaseLabel, Prerelease);
            }

            return Op switch {
                Operator.Equal => cmp == 0,
                Operator.Greater => cmp > 0,
                Operator.GreaterOrEqual => cmp >= 0,
                Operator.Less => cmp < 0,
                Operator.LessOrEqual => cmp <= 0,
                _ => false,
            };
        }
    }

    /// <summary>
    /// A version as written in a constraint: up to four components, any of them an
    /// <c>x</c>/<c>*</c> wildcard that ends the specified part, an optional <c>v</c> and an
    /// optional prerelease.
    /// </summary>
    private sealed record Partial(IReadOnlyList<long> Components, int Specified,
        string? Prerelease) {
        public static bool TryParse(string text, out Partial parsed) {
            parsed = new Partial([], 0, null);
            text = text.Trim();

            if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) {
                text = text[1..];
            }

            string? prerelease = null;
            var dash = text.IndexOf('-');
            if (dash >= 0) {
                prerelease = text[(dash + 1)..];
                text = text[..dash];
                if (prerelease.Length == 0) {
                    return false;
                }
            }

            if (text.Length == 0) {
                // An empty range means any version; an empty version with a prerelease means
                // nothing.
                return prerelease is null;
            }

            var pieces = text.Split('.');
            if (pieces.Length > 4) {
                return false;
            }

            var components = new List<long>();
            var wildcard = false;
            foreach (var piece in pieces) {
                if (piece is "x" or "X" or "*") {
                    wildcard = true;
                    continue;
                }

                // A number after a wildcard ("1.x.3") has no meaning.
                if (wildcard
                    || !long.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var value)) {
                    return false;
                }

                components.Add(value);
            }

            // A prerelease only makes sense on a complete version: "1.2-rc1" is refused, as in npm.
            if (prerelease is not null && components.Count < FullComponentCount) {
                return false;
            }

            parsed = new Partial(components, components.Count, prerelease);
            return true;
        }

        /// <summary>The first <paramref name="index"/> + 1 components, the last of them
        /// incremented.</summary>
        public IReadOnlyList<long> Bump(int index) {
            var bumped = Components.Take(index + 1).ToList();
            bumped[index]++;
            return bumped;
        }
    }
}
