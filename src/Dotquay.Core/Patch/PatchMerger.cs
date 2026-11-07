using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Patch;

/// <summary>
/// Per-file 3-way merge of three already-materialized directory trees — the "true git-style 3-way
/// merge" SPEC.md §10 promises, strictly better than patch-tools that can only fail (SPEC.md §10:
/// "because the lockfile records the exact base SHA and the cache holds the pristine copy, dotquay
/// can run a true git-style 3-way merge"). Deliberately knows nothing about where the three trees
/// came from (that's <see cref="PatchReapplier"/>'s job) or about the lockfile/patch files at all —
/// give it <c>base</c>/<c>ours</c>/<c>theirs</c> directories and a destination, and it produces the
/// merged tree plus the list of paths that came out conflicted.
/// </summary>
/// <remarks>
/// For each relative path across the union of all three trees, exactly one of eight
/// presence-combinations applies (base/ours/theirs each present or absent) — "absent from all
/// three" never occurs since the path came from the union. Each combination has a well-defined
/// outcome, mirroring how git itself treats adds/deletes/modifies during a real merge:
/// <list type="bullet">
/// <item>Only in ours, or only in theirs: a clean add from that side — keep it as-is.</item>
/// <item>Added in both (not in base): identical content is a clean add; different content is a
/// genuine add/add conflict, resolved via <see cref="MergeFileOrRecordConflictAsync"/> against a
/// synthesized empty "base" (the same trick git itself uses for add/add conflicts).</item>
/// <item>Deleted on one side, present in base and the other: if the surviving side matches base, the
/// delete wins cleanly (the other side never touched it); if it doesn't, that's a delete/modify
/// conflict — the surviving side's content is kept (never silently discarded) and the path is
/// flagged, since there's no "file" for <c>git merge-file</c> to merge a deletion against.</item>
/// <item>Deleted on both sides: a clean delete — omitted from the result, no conflict.</item>
/// <item>Present in all three: the standard case, resolved via
/// <see cref="MergeFileOrRecordConflictAsync"/>.</item>
/// </list>
/// </remarks>
public sealed class PatchMerger {
    private const string EmptyBaseFileName = ".dotquay-merge-empty-base";

    private readonly IFileSystem _fileSystem;
    private readonly IPatchTool _patchTool;

    public PatchMerger(IFileSystem fileSystem, IPatchTool patchTool) {
        _fileSystem = fileSystem;
        _patchTool = patchTool;
    }

    public async Task<PatchMergeResult> MergeAsync(
        string baseDirectory, string oursDirectory, string theirsDirectory, string destinationDirectory,
        CancellationToken cancellationToken = default) {
        _fileSystem.DeleteDirectory(destinationDirectory);
        _fileSystem.CreateDirectory(destinationDirectory);

        var basePaths = RelativeFilesOf(baseDirectory);
        var oursPaths = RelativeFilesOf(oursDirectory);
        var theirsPaths = RelativeFilesOf(theirsDirectory);
        var allPaths = basePaths.Union(oursPaths).Union(theirsPaths).OrderBy(p => p, StringComparer.Ordinal);

        // A single synthesized empty file, reused for every add/add conflict this call needs one
        // for — git merge-file requires all three file arguments to exist on disk (confirmed against
        // real git), so an add/add "no common ancestor" case needs a real (empty) stand-in, not a
        // path that simply doesn't exist.
        var emptyBasePath = _fileSystem.CombinePath(destinationDirectory, EmptyBaseFileName);
        _fileSystem.WriteAllBytes(emptyBasePath, []);

        var conflicts = new List<string>();

        try {
            foreach (var relativePath in allPaths) {
                var inBase = basePaths.Contains(relativePath);
                var inOurs = oursPaths.Contains(relativePath);
                var inTheirs = theirsPaths.Contains(relativePath);

                var basePath = _fileSystem.CombinePath(baseDirectory, relativePath);
                var oursPath = _fileSystem.CombinePath(oursDirectory, relativePath);
                var theirsPath = _fileSystem.CombinePath(theirsDirectory, relativePath);
                var destinationPath = _fileSystem.CombinePath(destinationDirectory, relativePath);

                if (!inBase && inOurs && !inTheirs) {
                    CopyFile(oursPath, destinationPath);
                } else if (!inBase && !inOurs && inTheirs) {
                    CopyFile(theirsPath, destinationPath);
                } else if (!inBase && inOurs && inTheirs) {
                    if (BytesEqual(oursPath, theirsPath)) {
                        CopyFile(oursPath, destinationPath);
                    } else if (await MergeFileOrRecordConflictAsync(emptyBasePath, oursPath, theirsPath, destinationPath, cancellationToken)) {
                        conflicts.Add(relativePath);
                    }
                } else if (inBase && !inOurs && inTheirs) {
                    // Deleted by our patch. Clean if upstream left it alone; otherwise a
                    // delete/modify conflict — keep upstream's edit rather than silently losing it.
                    if (!BytesEqual(basePath, theirsPath)) {
                        CopyFile(theirsPath, destinationPath);
                        conflicts.Add(relativePath);
                    }
                } else if (inBase && inOurs && !inTheirs) {
                    // Deleted upstream. Clean if our patch left it alone; otherwise a delete/modify
                    // conflict — keep our edit rather than silently losing it.
                    if (!BytesEqual(basePath, oursPath)) {
                        CopyFile(oursPath, destinationPath);
                        conflicts.Add(relativePath);
                    }
                } else if (inBase && !inOurs && !inTheirs) {
                    // Deleted on both sides — both agree, nothing to write.
                } else {
                    // Present in all three: the standard case.
                    if (BytesEqual(oursPath, theirsPath)) {
                        CopyFile(oursPath, destinationPath);
                    } else if (BytesEqual(basePath, oursPath)) {
                        // We never touched it — take upstream's change wholesale.
                        CopyFile(theirsPath, destinationPath);
                    } else if (BytesEqual(basePath, theirsPath)) {
                        // Upstream never touched it — keep our patch's change wholesale.
                        CopyFile(oursPath, destinationPath);
                    } else if (await MergeFileOrRecordConflictAsync(basePath, oursPath, theirsPath, destinationPath, cancellationToken)) {
                        conflicts.Add(relativePath);
                    }
                }
            }
        } finally {
            _fileSystem.DeleteFile(emptyBasePath);
        }

        return new PatchMergeResult(conflicts);
    }

    /// <summary>Seeds <paramref name="destinationPath"/> with "ours", then merges "theirs" into it in
    /// place via <see cref="IPatchTool.MergeFileAsync"/>. Returns whether the merge conflicted.</summary>
    private async Task<bool> MergeFileOrRecordConflictAsync(
        string basePath, string oursPath, string theirsPath, string destinationPath, CancellationToken cancellationToken) {
        CopyFile(oursPath, destinationPath);
        var conflictCount = await _patchTool.MergeFileAsync(destinationPath, basePath, theirsPath, cancellationToken);
        return conflictCount > 0;
    }

    private HashSet<string> RelativeFilesOf(string root) {
        if (!_fileSystem.DirectoryExists(root)) {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return _fileSystem.EnumerateFiles(root, recursive: true)
            .Select(file => NormalizeRelative(file, root))
            .ToHashSet(StringComparer.Ordinal);
    }

    private void CopyFile(string sourcePath, string destinationPath) =>
        _fileSystem.WriteAllBytes(destinationPath, _fileSystem.ReadAllBytes(sourcePath));

    private bool BytesEqual(string leftPath, string rightPath) =>
        _fileSystem.ReadAllBytes(leftPath).AsSpan().SequenceEqual(_fileSystem.ReadAllBytes(rightPath));

    private static string NormalizeRelative(string filePath, string root) =>
        filePath[root.Length..].TrimStart('/', '\\').Replace('\\', '/');
}
