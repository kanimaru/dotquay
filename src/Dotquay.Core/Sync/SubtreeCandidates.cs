using System.Text;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Doctor;

namespace Dotquay.Core.Sync;

/// <summary>Why a folder in the fetched repository looks like the addon that was being looked
/// for.</summary>
public enum SubtreeMatchKind {
    /// <summary>The same words as the guess, spelled in a different separator convention —
    /// <c>SproutyDialogs</c> against <c>sprouty_dialogs</c>.</summary>
    NameShape,

    /// <summary>Something that holds a <c>plugin.cfg</c>, offered only when it is the one and only
    /// such thing in the repository — whatever it is called.</summary>
    OnlyAddon,
}

/// <summary>One place in a fetched repository the addon could actually be.</summary>
/// <param name="Path">Repository-relative subtree path, i.e. the value <c>path</c> should be set
/// to. <c>"."</c> when the addon is the repository root.</param>
/// <param name="Folder">The folder name the addon must install into — its own
/// <c>res://addons/&lt;folder&gt;/</c> references are written against this, so it is a fact about
/// the addon and not a preference.</param>
/// <param name="Kind">Which rule found it, so a prompt can say what it actually observed rather
/// than asking the user to trust an unexplained suggestion.</param>
public sealed record SubtreeCandidate(string Path, string Folder, SubtreeMatchKind Kind) {
    /// <summary>The <c>reason</c> wire value (docs/DESIGN-v0.20-SUBTREE-GUESS.md §7). A fixed
    /// string and therefore a contract with the editor plugin, exactly like <c>DoctorIssueKind</c>:
    /// adding a third rule means teaching the plugin about it, not hoping it copes.</summary>
    public string Reason => Kind == SubtreeMatchKind.NameShape ? "name_shape" : "only_addon";

    /// <summary>Half a sentence, for a message that has already named the path it means.</summary>
    public string Explain(string guessedSegment) => Kind == SubtreeMatchKind.NameShape
        ? $"the same name as '{guessedSegment}' in a different separator convention"
        : Path == "."
            ? "the addon is the repository itself"
            : "the only addon in the repository";
}

/// <summary>What a repository offered when the configured subtree turned out not to be
/// there.</summary>
/// <param name="GuessedPath">The path that was looked for and isn't there.</param>
/// <param name="Candidates">Every place the addon could be, ordinally sorted so a message lists
/// them stably.</param>
/// <param name="Suggestion">The single candidate worth offering, or null when the repository gives
/// no clear answer and the message has to fall back to listing.</param>
public sealed record SubtreeFinding(
    string GuessedPath, IReadOnlyList<SubtreeCandidate> Candidates, SubtreeCandidate? Suggestion) {
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>The clause naming where the addon is, or that there is no obvious answer — shared
    /// by the prompt, the refusal, and <see cref="SubtreeExtractor"/>'s exception so all three
    /// describe the same repository the same way.</summary>
    public string DescribeWhereTheAddonIs() {
        if (Suggestion is not null) {
            var segment = SubtreeCandidates.LastSegment(GuessedPath);
            return $"its repository holds '{Suggestion.Path}' — {Suggestion.Explain(segment)}";
        }

        return HasCandidates
            ? "its repository holds several addons ("
                + string.Join(", ", Candidates.Select(c => $"'{c.Path}'"))
                + ") and none is obviously this one"
            : "nothing in its repository looks like an addon folder";
    }
}

/// <summary>
/// Finds where an addon actually lives inside its own repository, when the guessed subtree isn't
/// there (docs/DESIGN-v0.20-SUBTREE-GUESS.md).
/// </summary>
/// <remarks>
/// <para>
/// dotquay guesses <c>addons/&lt;name&gt;</c> from the repository's URL.
/// <see cref="SubtreeExtractor.ResolveSubtreePath"/> repairs that guess's <em>capitalisation</em>
/// and deliberately nothing else, which leaves the commonest disagreement in the Godot ecosystem
/// uncovered: a PascalCase repository name over a snake_case addon folder.
/// <c>SproutyLabs/SproutyDialogs</c> holds <c>addons/sprouty_dialogs</c>, which differs by more
/// than case, so the case correction declines and the user is left with an error naming only the
/// path that didn't work.
/// </para>
/// <para>
/// This finds the path that would have worked. It decides nothing: it reads directories and reports
/// what it saw, and every caller either asks a human first (<c>add</c>) or merely puts the answer
/// into a message (<see cref="SubtreeExtractor"/>). dotquay still never infers a layout it wasn't
/// told (SPEC.md §7.1, OQ-2) — it now offers to be told.
/// </para>
/// </remarks>
public static class SubtreeCandidates {
    /// <summary>Where a Godot project keeps its plugins.</summary>
    private const string AddonsFolder = GodotProjectRootChecker.AddonsFolder;

    /// <summary>The file Godot requires at an editor plugin's root — the structural test for "this
    /// directory is an addon", the same one <see cref="GodotProjectRootChecker"/> uses.</summary>
    private const string PluginMarker = GodotProjectRootChecker.PluginMarker;

    /// <summary>
    /// Returns what <paramref name="cachedRepoPath"/> offers in place of
    /// <paramref name="guessedSubtreePath"/>, or null when the guess is fine and there is nothing
    /// to suggest.
    /// </summary>
    /// <remarks>
    /// Null is also the answer for a guess that only resolves once
    /// <see cref="SubtreeExtractor.ResolveSubtreePath"/> has fixed its capitalisation — that is
    /// already handled, and re-reporting it here would ask the user to confirm a correction dotquay
    /// makes silently and correctly.
    /// </remarks>
    public static SubtreeFinding? Find(IFileSystem fileSystem, string cachedRepoPath,
        string guessedSubtreePath) {
        if (!fileSystem.DirectoryExists(cachedRepoPath)) {
            return null;
        }

        var resolved = SubtreeExtractor.ResolveSubtreePath(fileSystem, cachedRepoPath,
            guessedSubtreePath);
        var resolvedFull = resolved is "." or ""
            ? cachedRepoPath
            : fileSystem.CombinePath(cachedRepoPath, resolved);

        if (fileSystem.DirectoryExists(resolvedFull)) {
            return null;
        }

        var wanted = LastSegment(guessedSubtreePath);
        var candidates = Collect(fileSystem, cachedRepoPath, wanted);

        return new SubtreeFinding(guessedSubtreePath, candidates, Choose(candidates));
    }

    /// <summary>
    /// The one candidate worth offering: a single name-shape match, else a single sole-addon match.
    /// </summary>
    /// <remarks>
    /// Ambiguity is answered with silence on purpose. Two candidates mean the repository has not
    /// told dotquay which addon this is, and picking one anyway is the layout inference this whole
    /// area exists not to do — the message lists them instead, which is a question the user can
    /// answer in one flag.
    /// </remarks>
    private static SubtreeCandidate? Choose(IReadOnlyList<SubtreeCandidate> candidates) {
        var byShape = candidates.Where(c => c.Kind == SubtreeMatchKind.NameShape).ToList();
        if (byShape.Count > 0) {
            return byShape.Count == 1 ? byShape[0] : null;
        }

        var soleAddon = candidates.Where(c => c.Kind == SubtreeMatchKind.OnlyAddon).ToList();
        return soleAddon.Count == 1 ? soleAddon[0] : null;
    }

    /// <summary>
    /// Every directory in the repository that could be the addon, under both rules.
    /// </summary>
    /// <remarks>
    /// The three searched shapes are tried in order and the first that yields anything wins, so a
    /// repository with a real <c>addons/</c> is never also offered its own root or a nested
    /// project's plugins. Mixing them would turn one clear answer into a list, which
    /// <see cref="Choose"/> reads as "no suggestion".
    /// </remarks>
    private static List<SubtreeCandidate> Collect(IFileSystem fileSystem, string repoPath,
        string wanted) {
        var folders = AddonFoldersIn(fileSystem, repoPath, AddonsFolder);

        if (folders.Count == 0
            && fileSystem.FileExists(fileSystem.CombinePath(repoPath, PluginMarker))) {
            // The addon *is* the repository. SPEC.md §7.1 requires `path = "."` to be stated rather
            // than inferred, which is exactly the statement this suggestion offers to make. The
            // folder stays the name that was being looked for: the cache directory this was read
            // from is named after a URL and a commit, and nothing in a root-level addon says what
            // it wants to be called.
            return [new SubtreeCandidate(".", wanted, SubtreeMatchKind.OnlyAddon)];
        }

        if (folders.Count == 0) {
            folders = NestedAddonFolders(fileSystem, repoPath);
        }

        var candidates = new List<SubtreeCandidate>();
        var normalizedWanted = Normalize(wanted);

        foreach (var (path, name, hasPlugin) in folders) {
            if (normalizedWanted.Length > 0 && Normalize(name) == normalizedWanted) {
                candidates.Add(new SubtreeCandidate(path, name, SubtreeMatchKind.NameShape));
            } else if (hasPlugin) {
                // Not yet known to be the *only* addon — Choose decides that. Collected either way,
                // because a list of them is what the message falls back to when it can't suggest.
                candidates.Add(new SubtreeCandidate(path, name, SubtreeMatchKind.OnlyAddon));
            }
        }

        candidates.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return candidates;
    }

    /// <summary>Immediate children of <paramref name="container"/> inside the repository, with
    /// whether each holds a <c>plugin.cfg</c>.</summary>
    private static List<(string Path, string Name, bool HasPlugin)> AddonFoldersIn(
        IFileSystem fileSystem, string repoPath, string container) {
        var found = new List<(string, string, bool)>();
        var resolvedContainer = SubtreeExtractor.ResolveSubtreePath(fileSystem, repoPath,
            container);
        var containerPath = fileSystem.CombinePath(repoPath, resolvedContainer);

        if (!fileSystem.DirectoryExists(containerPath)) {
            return found;
        }

        foreach (var directory in fileSystem.EnumerateDirectories(containerPath)) {
            var name = LastSegment(directory);
            var hasPlugin = fileSystem.FileExists(fileSystem.CombinePath(directory, PluginMarker));
            found.Add(($"{resolvedContainer}/{name}", name, hasPlugin));
        }

        return found;
    }

    /// <summary>
    /// <c>&lt;anything&gt;/addons/*</c>, one level down.
    /// </summary>
    /// <remarks>
    /// A repository that keeps its Godot project in a subfolder — <c>godot/addons/vbeami</c> — has
    /// no <c>addons/</c> at its root at all, so without this the common "addon repo that is also a
    /// playable project" layout offers nothing. One level only: deeper than that, a match stops
    /// being the repository's structure and starts being a coincidence.
    /// </remarks>
    private static List<(string Path, string Name, bool HasPlugin)> NestedAddonFolders(
        IFileSystem fileSystem, string repoPath) {
        var found = new List<(string, string, bool)>();

        foreach (var directory in fileSystem.EnumerateDirectories(repoPath)) {
            var child = LastSegment(directory);

            // git's own metadata and dotquay's cache bookkeeping are not repository content.
            if (child.StartsWith('.')) {
                continue;
            }

            found.AddRange(AddonFoldersIn(fileSystem, repoPath, $"{child}/{AddonsFolder}"));
        }

        return found;
    }

    /// <summary>
    /// Reduces a folder or repository name to its letters and digits, lowercased: the comparison
    /// that makes <c>SproutyDialogs</c>, <c>sprouty_dialogs</c> and <c>sprouty-dialogs</c> one
    /// name.
    /// </summary>
    /// <remarks>
    /// Separators are dropped rather than normalised to a common one, which is the same thing said
    /// more simply: the convention is what's being ignored, and "no separators at all"
    /// (<c>sproutydialogs</c>) is one of the conventions. What survives is the letters in order, so
    /// two names match exactly when they are the same word spelled the same way — a stricter test
    /// than it looks, and deliberately blind to nothing else.
    /// </remarks>
    public static string Normalize(string name) {
        var builder = new StringBuilder(name.Length);

        foreach (var character in name) {
            if (char.IsLetterOrDigit(character)) {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>The last path segment, separator-agnostic and trailing-slash tolerant.</summary>
    internal static string LastSegment(string path) {
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }
}
