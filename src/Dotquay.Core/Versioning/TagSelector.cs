using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Versioning;

/// <summary>Picks the highest tag satisfying a <see cref="VersionConstraint"/> out of a repo's
/// remote refs — the "matched against git tags" step of resolving an addon's <c>version</c> spec
/// (SPEC.md §7.1).</summary>
public static class TagSelector {
    public sealed record Match(GitRef Ref, AddonVersion Version);

    public static Match? SelectHighestMatching(IEnumerable<GitRef> refs, VersionConstraint constraint) {
        Match? best = null;

        foreach (var gitRef in refs) {
            if (gitRef.Kind != GitRefKind.Tag) {
                continue;
            }

            if (!AddonVersion.TryParse(gitRef.Name, out var version) || version is null) {
                continue;
            }

            if (!constraint.IsSatisfiedBy(version)) {
                continue;
            }

            if (best is null || version.CompareTo(best.Version) > 0) {
                best = new Match(gitRef, version);
            }
        }

        return best;
    }
}
