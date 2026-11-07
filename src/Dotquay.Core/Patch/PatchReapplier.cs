using Dotquay.Core.Abstractions;
using Dotquay.Core.Sync;

namespace Dotquay.Core.Patch;

/// <summary>
/// Orchestrates re-applying a captured patch on top of a freshly-fetched new version (SPEC.md §10
/// step 3, requirement P1-1): reconstructs "ours" (the old pristine base + the patch, via
/// <see cref="IPatchTool.ApplyPatchAsync"/> — never the currently-installed tree, so the merge
/// reflects exactly what <c>dotquay patch</c> captured, not whatever else might be sitting in
/// <c>addons/&lt;name&gt;</c>) and 3-way merges it against the already-extracted new version
/// ("theirs", supplied by the caller) using <see cref="PatchMerger"/>, overwriting the new
/// version's staging tree with the result in place.
/// </summary>
public sealed class PatchReapplier {
    private readonly IFileSystem _fileSystem;
    private readonly RepoCache _cache;
    private readonly IPatchTool _patchTool;
    private readonly PatchMerger _merger;

    public PatchReapplier(IFileSystem fileSystem, RepoCache cache, IPatchTool patchTool) {
        _fileSystem = fileSystem;
        _cache = cache;
        _patchTool = patchTool;
        _merger = new PatchMerger(fileSystem, patchTool);
    }

    /// <summary>
    /// Reapplies <paramref name="patchText"/> on top of <paramref name="newVersionStagingPath"/>
    /// (already extracted at the new resolved SHA by the caller — this becomes "theirs"),
    /// overwriting it in place with the merge result. Returns the conflicted relative paths (empty
    /// means clean). <paramref name="patchBaseSha"/>/<paramref name="baseSubtreePath"/> are the SHA
    /// and subtree the patch was captured against (<see cref="Lockfile.LockedAddon.PatchBaseSha"/>/
    /// <see cref="Lockfile.LockedAddon.Path"/> at capture time) — <em>not</em> necessarily the
    /// addon's current <c>Path</c>, though in practice they're almost always the same value.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReapplyAsync(
        string repositoryUrl,
        string patchBaseSha,
        string baseSubtreePath,
        string patchText,
        string newVersionStagingPath,
        CancellationToken cancellationToken = default) {
        var oldBaseStagingPath = $"{newVersionStagingPath}-patch-old-base";
        var oursStagingPath = $"{newVersionStagingPath}-patch-ours";
        var mergedStagingPath = $"{newVersionStagingPath}-patch-merged";

        try {
            var cachedOldBase = await _cache.GetOrFetchAsync(repositoryUrl, patchBaseSha,
                cancellationToken);
            SubtreeExtractor.Extract(_fileSystem, cachedOldBase, baseSubtreePath,
                oldBaseStagingPath);

            CopyDirectory(oldBaseStagingPath, oursStagingPath);
            await _patchTool.ApplyPatchAsync(patchText, oursStagingPath, cancellationToken);

            var mergeResult = await _merger.MergeAsync(
                oldBaseStagingPath, oursStagingPath, newVersionStagingPath, mergedStagingPath,
                cancellationToken);

            // Overwrite the new version's staging tree with the merge result either way — a
            // conflicted result still needs to land in staging with its markers per SPEC.md §10
            // step 3 ("Conflict -> <<<<<<</>>>>>>> markers in staging, sync halts... resolve in
            // staging").
            _fileSystem.MoveDirectory(mergedStagingPath, newVersionStagingPath);

            return mergeResult.ConflictedPaths;
        } finally {
            _fileSystem.DeleteDirectory(oldBaseStagingPath);
            _fileSystem.DeleteDirectory(oursStagingPath);

            // Only survives the try block if MoveDirectory above never ran (an exception was
            // thrown before it) — a no-op otherwise, since MoveDirectory already emptied it out.
            _fileSystem.DeleteDirectory(mergedStagingPath);
        }
    }

    /// <summary>Directory copy via <see cref="IFileSystem"/> rather than real OS calls — so this
    /// works identically against <c>SystemFileSystem</c> in production and an in-memory test
    /// double, unlike <c>ProcessPatchTool</c>'s own directory copy (which must use real OS paths,
    /// since it shells out to a real <c>git</c> process that only understands the real
    /// filesystem).</summary>
    private void CopyDirectory(string sourceDirectory, string destinationDirectory) {
        _fileSystem.DeleteDirectory(destinationDirectory);
        _fileSystem.CreateDirectory(destinationDirectory);

        foreach (var file in _fileSystem.EnumerateFiles(sourceDirectory, recursive: true)
            .ToList()) {
            var relative = file[sourceDirectory.Length..].TrimStart('/', '\\').Replace('\\', '/');
            _fileSystem.WriteAllBytes(_fileSystem.CombinePath(destinationDirectory, relative),
                _fileSystem.ReadAllBytes(file));
        }
    }
}
