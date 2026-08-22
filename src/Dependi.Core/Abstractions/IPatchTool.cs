namespace Dependi.Core.Abstractions;

/// <summary>
/// Shells out to <c>git</c>'s local diff/apply/merge plumbing — the three primitives the patch
/// workflow (SPEC.md §10, requirement P1-1) is built from. Unlike <see cref="IGitClient"/>, nothing
/// here touches the network or a real repository; it's pure local file-tree diffing and 3-way
/// merging, kept as its own interface rather than folded into <see cref="IGitClient"/> so that
/// interface stays scoped to "remote git operations" (SPEC.md §12's original rationale for it).
/// </summary>
public interface IPatchTool
{
    /// <summary>Diffs two directory trees and returns the unified diff text (empty string if
    /// identical) — captures a patch (SPEC.md §10 step 2: "diffs against the pristine cache copy").</summary>
    Task<string> DiffDirectoriesAsync(string baseDirectory, string oursDirectory, CancellationToken cancellationToken = default);

    /// <summary>Applies a unified diff (as produced by <see cref="DiffDirectoriesAsync"/>) to a
    /// directory tree in place. Used to reconstruct "ours" (pristine base + captured patch) at
    /// re-apply time — this should always succeed for a patch captured against the exact base it's
    /// being applied to; failure here means the patch or cache is corrupted, not a normal conflict
    /// (those come from <see cref="MergeFileAsync"/> instead).</summary>
    Task ApplyPatchAsync(string patchText, string targetDirectory, CancellationToken cancellationToken = default);

    /// <summary>3-way merges three real files into <paramref name="currentPath"/> in place — git's
    /// own <c>merge-file &lt;current&gt; &lt;base&gt; &lt;other&gt;</c> semantics exactly (deliberately
    /// not named ours/base/theirs: callers decide which side plays which role).
    /// <see cref="Patch.PatchMerger"/> always seeds <paramref name="currentPath"/> with "ours" (the
    /// patch reconstructed against its recorded base) and passes upstream's new version as
    /// <paramref name="otherPath"/> — so the result is "our patch, with upstream's changes merged
    /// in", not the other way around. <paramref name="currentPath"/> is both an input and the
    /// output. Returns the conflict count: 0 means clean.</summary>
    Task<int> MergeFileAsync(string currentPath, string basePath, string otherPath, CancellationToken cancellationToken = default);
}
