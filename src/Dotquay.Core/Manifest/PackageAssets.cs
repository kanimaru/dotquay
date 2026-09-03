namespace Dotquay.Core.Manifest;

/// <summary>
/// NuGet asset-flow metadata for one <c>PackageReference</c> — the
/// <c>IncludeAssets</c>/<c>ExcludeAssets</c>/<c>PrivateAssets</c> trio
/// (docs/DESIGN-v0.10-PACKAGE-ASSETS.md).
/// </summary>
/// <remarks>
/// <para>
/// Each list is null when the manifest did not declare it, which is <em>not</em> the same as
/// declaring NuGet's default: null means "dotquay has no opinion, leave whatever the .csproj has
/// alone". Only a declared list is written, replaced, or removed.
/// </para>
/// <para>
/// Tags are stored close to verbatim — canonically cased when recognised, untouched when not.
/// Unknown tags are deliberately preserved rather than dropped: MSBuild may gain tags after this
/// code was written, and silently discarding one would be worse than passing it through. They are
/// reported via <see cref="UnknownTags"/> so a caller with a warnings channel can say so.
/// </para>
/// </remarks>
public sealed record PackageAssets
{
    /// <summary>The complete set MSBuild recognises, in canonical casing. `all` and `none` are
    /// included here but may only appear alone — see <see cref="FindMisuseOfAllOrNone"/>.</summary>
    public static readonly IReadOnlyList<string> KnownTags =
    [
        "compile",
        "runtime",
        "contentFiles",
        "build",
        "buildMultitargeting",
        "buildTransitive",
        "analyzers",
        "native",
        "none",
        "all",
    ];

    /// <summary>Nothing declared — the overwhelmingly common case.</summary>
    public static readonly PackageAssets None = new();

    /// <summary><c>IncludeAssets</c>: the assets this project consumes. MSBuild default: <c>all</c>.</summary>
    public IReadOnlyList<string>? Include { get; init; }

    /// <summary><c>ExcludeAssets</c>: subtracted from the above. MSBuild default: <c>none</c>.</summary>
    public IReadOnlyList<string>? Exclude { get; init; }

    /// <summary><c>PrivateAssets</c>: consumed here but not flowed to the parent project. MSBuild
    /// default: <c>contentfiles;analyzers;build</c>.</summary>
    public IReadOnlyList<string>? Private { get; init; }

    /// <summary>True when the manifest declared none of the three, so the csproj writer has nothing
    /// to own for this package.</summary>
    public bool IsEmpty => Include is null && Exclude is null && Private is null;

    /// <summary>Every declared tag this build does not recognise, across all three lists, de-duplicated.
    /// Empty for the normal case. Callers with a warnings channel report these; nothing rejects them.</summary>
    public IReadOnlyList<string> UnknownTags
    {
        get
        {
            List<string>? unknown = null;

            foreach (var list in new[] { Include, Exclude, Private })
            {
                if (list is null)
                {
                    continue;
                }

                foreach (var tag in list)
                {
                    if (IsKnown(tag))
                    {
                        continue;
                    }

                    unknown ??= [];
                    if (!unknown.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        unknown.Add(tag);
                    }
                }
            }

            return unknown ?? [];
        }
    }

    public static bool IsKnown(string tag) =>
        KnownTags.Any(known => string.Equals(known, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Canonical casing for a recognised tag; the input untouched otherwise, so an unknown
    /// tag survives a round-trip exactly as the author typed it.</summary>
    public static string Canonicalize(string tag) =>
        KnownTags.FirstOrDefault(known => string.Equals(known, tag, StringComparison.OrdinalIgnoreCase)) ?? tag;

    /// <summary>
    /// Canonicalises every tag and drops duplicates, preserving declaration order.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IReadOnlyList<string> tags)
    {
        var result = new List<string>(tags.Count);

        foreach (var tag in tags)
        {
            var canonical = Canonicalize(tag.Trim());
            if (canonical.Length > 0 && !result.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(canonical);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the offending tag when <c>all</c> or <c>none</c> appears alongside anything else, or
    /// null when the list is fine.
    /// </summary>
    /// <remarks>
    /// NuGet's own rule: "multiple values separated by a semicolon except for `all` and `none`, which
    /// must appear by themselves". A list like <c>["all", "runtime"]</c> has no meaning MSBuild will
    /// honour, so this is a hard parse error rather than something to normalise into a guess.
    /// </remarks>
    public static string? FindMisuseOfAllOrNone(IReadOnlyList<string> tags)
    {
        if (tags.Count < 2)
        {
            return null;
        }

        return tags.FirstOrDefault(tag =>
            string.Equals(tag, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tag, "none", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The MSBuild spelling of a tag list: semicolon-separated, no spaces.</summary>
    public static string Format(IReadOnlyList<string> tags) => string.Join(';', tags);

    /// <summary>
    /// Reads an MSBuild tag list back out of a <c>.csproj</c>. Tolerates the spacing real files carry
    /// (<c>"runtime; build; native"</c>) and canonicalises, so a hand-written value and a
    /// dotquay-written one compare equal instead of reading as drift.
    /// </summary>
    public static IReadOnlyList<string> Parse(string value) =>
        Normalize(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Whether two declared lists mean the same thing — order-insensitive, case-insensitive,
    /// and treating null and empty alike. Used for drift detection, where a spelling difference is not
    /// a difference.</summary>
    public static bool SameTags(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Count == right.Count
            && left.All(tag => right.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }

    public bool SameAs(PackageAssets other) =>
        SameTags(Include, other.Include)
        && SameTags(Exclude, other.Exclude)
        && SameTags(Private, other.Private);
}
