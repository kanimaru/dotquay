using Dotquay.Core.Git;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Workflows;

/// <summary>Where the manifest key <c>add</c> chose for a git addon came from.</summary>
public enum AddonNameSource {
    /// <summary>The caller named it outright (<c>--name</c>).</summary>
    Explicit,

    /// <summary>The repository's own name — the historical default, and still the first
    /// choice.</summary>
    Url,

    /// <summary>The URL's name was taken by a different addon, so the requested install folder
    /// named it.</summary>
    InstallAs,

    /// <summary>The URL's name was taken by a different addon, so the last segment of <c>--path</c>
    /// named it.</summary>
    PathLeaf,
}

/// <summary>The manifest key an <c>add</c> will write, and why.</summary>
/// <param name="Name">The <c>[addons.&lt;name&gt;]</c> key.</param>
/// <param name="Source">Which rule produced it.</param>
/// <param name="DisplacedBy">For a fallback, the URL-derived name that was already taken.</param>
public sealed record AddonNameChoice(string Name, AddonNameSource Source,
    string? DisplacedBy = null) {
    /// <summary>True when the name is neither what the user typed as <c>--name</c> nor what the URL
    /// alone gives — worth one line of output, since nobody asked for it by that name.</summary>
    public bool IsFallback => Source is AddonNameSource.InstallAs or AddonNameSource.PathLeaf;
}

/// <summary>
/// The single authority on which <c>[addons.&lt;name&gt;]</c> key <c>dotquay add</c> writes for a
/// git URL.
/// </summary>
/// <remarks>
/// <para>Before this existed the name was the repository's name and nothing else, so a repository
/// that ships several addons (<c>vbeami</c> ships <c>vbeami</c>, <c>vbeami_gdmp</c>,
/// <c>vbeami_vcam</c>, …) could have exactly one of them added — every further <c>add --path
/// godot/addons/vbeami_vcam</c> was refused with "'vbeami' is already declared", even though it
/// names a different subtree going into a different folder. The only way round it was writing the
/// manifest entry by hand.</para>
/// <para>The rules, in order:</para>
/// <list type="number">
/// <item><description><c>--name</c> wins outright, and is refused if taken.</description></item>
/// <item><description>Otherwise the URL's name, exactly as before — every add that worked before
/// this change writes the very same key (<c>--path</c> alone still does <em>not</em> rename an
/// addon whose URL name is free).</description></item>
/// <item><description>Only when that name is taken by a <em>different</em> addon — another subtree
/// of the same repo, or another repo that happens to share its name — does it fall back to
/// <c>--install-as</c>, then the last segment of <c>--path</c>. Both are things the user typed and
/// both already describe the addon's own folder, so neither name is invented.</description></item>
/// <item><description>Re-adding the <em>same</em> addon (same repository and same subtree, or no
/// subtree named at all, which means "the one you already have") keeps the "use update" refusal:
/// falling back there would quietly install a second copy of it.</description></item>
/// </list>
/// <para>Pure — it reads only the manifest's addon table — so <c>add</c> and <c>add --preview</c>
/// share it verbatim and can never disagree about the name.</para>
/// </remarks>
public static class AddonNaming {
    /// <summary>Chooses the manifest key, or throws <see cref="WorkflowException"/> naming the flag
    /// that fixes it.</summary>
    public static AddonNameChoice Choose(
        string gitUrl,
        AddonAddOptions options,
        IReadOnlyDictionary<string, AddonSpec> declared,
        string manifestPath) {
        ArgumentNullException.ThrowIfNull(gitUrl);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(declared);

        if (options.Name is { } explicitName) {
            return ChooseExplicit(explicitName, declared, manifestPath);
        }

        var urlName = DeriveFromUrl(gitUrl);

        if (!declared.TryGetValue(urlName, out var existing)) {
            return new AddonNameChoice(urlName, AddonNameSource.Url);
        }

        if (IsSameAddon(existing, gitUrl, options.Path)) {
            throw new WorkflowException(
                $"'{urlName}' is already declared in {manifestPath}. Use 'dotquay update "
                + $"{urlName}' instead.");
        }

        var candidates = FallbackCandidates(options, urlName).ToList();

        foreach (var (candidate, source) in candidates) {
            if (!declared.ContainsKey(candidate)) {
                return new AddonNameChoice(candidate, source, DisplacedBy: urlName);
            }
        }

        var taken = new[] { urlName }.Concat(candidates.Select(c => c.Name)).ToList();

        throw new WorkflowException(
            $"{Quote(taken)} {(taken.Count == 1 ? "is" : "are")} already declared in "
            + $"{manifestPath}, by a different addon than this one ({Describe(existing)}). "
            + "Give this one its own name with --name <name>"
            + (options.InstallAs is null ? ", or with --install-as <folder>, which also names it."
                : "."));
    }

    /// <summary>Derives a manifest key from a git URL, e.g.
    /// <c>"https://github.com/nathanhoad/godot_dialogue_manager.git"</c> ->
    /// <c>"godot_dialogue_manager"</c>.</summary>
    public static string DeriveFromUrl(string gitUrl) {
        ArgumentNullException.ThrowIfNull(gitUrl);

        var trimmed = gitUrl.TrimEnd('/');
        var lastSeparator = trimmed.LastIndexOfAny(['/', ':']);
        var last = lastSeparator < 0 ? trimmed : trimmed[(lastSeparator + 1)..];
        return last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
    }

    /// <summary>The last segment of a subtree path, or null for the repo root (<c>"."</c>) or no
    /// path.</summary>
    public static string? PathLeaf(string? subtreePath) {
        if (subtreePath is null) {
            return null;
        }

        var trimmed = subtreePath.Replace('\\', '/').Trim('/');
        var leaf = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return leaf is "" or "." or ".." ? null : leaf;
    }

    /// <summary>
    /// Whether a name can be written as an <c>[addons.&lt;name&gt;]</c> key. It doubles as the
    /// install folder when no <c>install_as</c> is set, so it passes the same test
    /// <c>install_as</c> does — and carries no dot, which TOML would read as a nested table.
    /// </summary>
    public static bool IsValidName(string name) =>
        ManifestReader.IsSafeFolderName(name) && !name.Contains('.', StringComparison.Ordinal);

    private static AddonNameChoice ChooseExplicit(
        string name, IReadOnlyDictionary<string, AddonSpec> declared, string manifestPath) {
        if (!IsValidName(name)) {
            throw new WorkflowException(
                $"--name \"{name}\" is not a usable addon name. It must be a single plain folder "
                + "name — no slashes, dots or drive letters — because it becomes the "
                + "[addons.<name>] key and, without --install-as, the folder under addons/.");
        }

        if (declared.ContainsKey(name)) {
            throw new WorkflowException(
                $"'{name}' is already declared in {manifestPath}. Pick a different --name, or use "
                + $"'dotquay update {name}' if it is the same addon.");
        }

        return new AddonNameChoice(name, AddonNameSource.Explicit);
    }

    private static IEnumerable<(string Name, AddonNameSource Source)> FallbackCandidates(
        AddonAddOptions options, string urlName) {
        var seen = new HashSet<string>(StringComparer.Ordinal) { urlName };

        if (options.InstallAs is { } installAs && IsValidName(installAs) && seen.Add(installAs)) {
            yield return (installAs, AddonNameSource.InstallAs);
        }

        if (PathLeaf(options.Path) is { } leaf && IsValidName(leaf) && seen.Add(leaf)) {
            yield return (leaf, AddonNameSource.PathLeaf);
        }
    }

    /// <summary>
    /// Same repository and same subtree. A request naming no subtree means "the addon I already
    /// have from this repo", so it counts as the same — the accidental re-add the refusal exists
    /// for.
    /// </summary>
    private static bool IsSameAddon(AddonSpec existing, string gitUrl, string? requestedPath) {
        if (!SameRepository(existing.Git, gitUrl)) {
            return false;
        }

        return requestedPath is null
            || string.Equals(
                NormalizeSubtree(existing.Path ?? $"addons/{existing.Name}"),
                NormalizeSubtree(requestedPath),
                StringComparison.Ordinal);
    }

    private static bool SameRepository(string left, string right) =>
        GitUrl.TryParse(left, out var a) && GitUrl.TryParse(right, out var b)
            ? a!.SameRepositoryAs(b!)
            : string.Equals(left.TrimEnd('/'), right.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSubtree(string path) {
        var trimmed = path.Replace('\\', '/').Trim('/');
        return trimmed.Length == 0 ? "." : trimmed;
    }

    private static string Describe(AddonSpec existing) =>
        existing.Path is null ? existing.Git : $"{existing.Git} at {existing.Path}";

    private static string Quote(IEnumerable<string> names) =>
        string.Join(" and ", names.Select(n => $"'{n}'"));
}
