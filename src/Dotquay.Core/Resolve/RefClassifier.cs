using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Resolve;

/// <summary>What a bare ref written after <c>#</c> on an <c>add</c> turned out to be.</summary>
/// <param name="Kind">Which manifest field it maps to.</param>
/// <param name="Value">The value to write — the branch name, the tag, or the commit SHA.</param>
public sealed record ClassifiedRef(AddonSourceKind Kind, string Value);

/// <summary>
/// Works out whether <c>dotquay add &lt;url&gt;#main</c> means a branch, a tag, or a commit, by asking
/// the remote (SPEC.md §6).
/// </summary>
/// <remarks>
/// The shorthand exists because "give me this repo on main" is the single most common thing someone
/// wants that the tag-seeking default doesn't do. Guessing from the string alone would be wrong often
/// enough to matter — <c>1.0</c> is a plausible branch name and <c>release</c> is a plausible tag — so
/// this asks git rather than pattern-matching, and reports what it found when it can't decide.
/// </remarks>
public sealed class RefClassifier {
    private readonly IGitClient _gitClient;

    public RefClassifier(IGitClient gitClient) {
        _gitClient = gitClient;
    }

    public async Task<ClassifiedRef> ClassifyAsync(string gitUrl, string reference, CancellationToken cancellationToken = default) {
        var trimmed = reference.Trim();

        if (trimmed.Length == 0) {
            throw new AddonResolutionException("An empty ref was given after '#'.");
        }

        // A full SHA is unambiguous and, unlike a branch or tag, need not still exist as a named ref —
        // pinning to a commit reachable only from history is a legitimate thing to ask for.
        if (LooksLikeFullSha(trimmed)) {
            return new ClassifiedRef(AddonSourceKind.Rev, trimmed);
        }

        var refs = await _gitClient.ListRemoteRefsAsync(gitUrl, cancellationToken);

        // Branch before tag: when a repository has both a `main` branch and a `main` tag, the branch
        // is overwhelmingly what someone typing "#main" meant.
        var branch = refs.FirstOrDefault(r => r.Kind == GitRefKind.Branch && r.Name == trimmed);
        if (branch is not null) {
            return new ClassifiedRef(AddonSourceKind.Branch, trimmed);
        }

        var tag = refs.FirstOrDefault(r => r.Kind == GitRefKind.Tag && r.Name == trimmed);
        if (tag is not null) {
            // An exact tag, not a range. Someone naming one specific tag asked for that tag; widening
            // it to "~2.5" would silently install something they didn't type.
            return new ClassifiedRef(AddonSourceKind.Version, trimmed);
        }

        // Not a named ref. A short hex string is almost certainly an abbreviated commit — accept it as
        // a rev rather than refusing, since `git` itself resolves those happily.
        if (LooksLikeShortSha(trimmed)) {
            return new ClassifiedRef(AddonSourceKind.Rev, trimmed);
        }

        throw new AddonResolutionException(
            $"'{trimmed}' is not a branch, tag, or commit in {gitUrl}. "
            + DescribeAvailable(refs));
    }

    private static string DescribeAvailable(IReadOnlyList<GitRef> refs) {
        var branches = refs.Where(r => r.Kind == GitRefKind.Branch).Select(r => r.Name).Take(5).ToList();
        var tags = refs.Where(r => r.Kind == GitRefKind.Tag).Select(r => r.Name).TakeLast(5).ToList();

        if (branches.Count == 0 && tags.Count == 0) {
            return "That repository reports no branches or tags at all.";
        }

        var parts = new List<string>();
        if (branches.Count > 0) {
            parts.Add($"branches: {string.Join(", ", branches)}");
        }

        if (tags.Count > 0) {
            parts.Add($"recent tags: {string.Join(", ", tags)}");
        }

        return "Available — " + string.Join("; ", parts) + ".";
    }

    private static bool LooksLikeFullSha(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    /// <summary>Seven is git's own default abbreviation length, and below that a hex string is as
    /// likely to be a branch name as a commit.</summary>
    private static bool LooksLikeShortSha(string value) =>
        value.Length is >= 7 and < 40 && value.All(Uri.IsHexDigit);
}
