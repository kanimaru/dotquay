namespace Dotquay.Core.Patch;

/// <summary>Result of <see cref="PatchMerger.MergeAsync"/> — SPEC.md §10 step 3 / requirement
/// P1-1's "conflicts detected like git does". Conflicting paths are relative to the three directory
/// trees that were merged (not yet written under <c>addons/&lt;name&gt;</c> — the caller decides
/// what to do with a non-empty result, per SPEC.md: leave <c>addons/</c> untouched and point at
/// staging).</summary>
public sealed record PatchMergeResult(IReadOnlyList<string> ConflictedPaths) {
    public bool HasConflicts => ConflictedPaths.Count > 0;
}
