using Dependi.Core.Abstractions;

namespace Dependi.Core.Sync;

/// <summary>
/// Extracts an addon's <c>path</c> subtree out of its fetched repo into a staging folder — solves
/// the "addon is not at repo root" problem without submodules (SPEC.md §9 step 2). Binary-safe:
/// copies bytes, never round-trips file contents through text encoding.
/// </summary>
public static class SubtreeExtractor
{
    /// <summary>Never copied into staging — git's own metadata and dependi's cache bookkeeping,
    /// not part of the addon itself.</summary>
    private static readonly string[] ExcludedTopLevelSegments = [".git", ".dependi-fetch-complete"];

    public static void Extract(IFileSystem fileSystem, string cachedRepoPath, string relativeSubtreePath, string stagingPath)
    {
        var sourceRoot = relativeSubtreePath is "." or ""
            ? cachedRepoPath
            : fileSystem.CombinePath(cachedRepoPath, relativeSubtreePath);

        if (!fileSystem.DirectoryExists(sourceRoot))
        {
            throw new SyncException(
                $"Configured path '{relativeSubtreePath}' does not exist in the fetched repository at {cachedRepoPath}.");
        }

        fileSystem.DeleteDirectory(stagingPath);
        fileSystem.CreateDirectory(stagingPath);

        var copiedAnyFile = false;

        // Materialized up front: writing into staging while still lazily enumerating the source
        // tree can mutate the same underlying store mid-iteration (the in-memory test double hits
        // this directly; a real filesystem enumerator can behave just as unpredictably if entries
        // are added to a directory it's still walking).
        var sourceFiles = fileSystem.EnumerateFiles(sourceRoot, recursive: true).ToList();

        foreach (var file in sourceFiles)
        {
            var relative = NormalizeRelative(file, sourceRoot);

            if (IsExcluded(relative))
            {
                continue;
            }

            var destination = fileSystem.CombinePath(stagingPath, relative);
            fileSystem.WriteAllBytes(destination, fileSystem.ReadAllBytes(file));
            copiedAnyFile = true;
        }

        if (!copiedAnyFile)
        {
            throw new SyncException(
                $"Configured path '{relativeSubtreePath}' exists but is empty (after excluding git metadata) in the fetched repository at {cachedRepoPath}.");
        }
    }

    private static bool IsExcluded(string relativePath)
    {
        var firstSegment = relativePath.Split(['/', '\\'], 2)[0];
        return ExcludedTopLevelSegments.Contains(firstSegment, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeRelative(string filePath, string sourceRoot)
    {
        var relative = filePath[sourceRoot.Length..].TrimStart('/', '\\');
        return relative.Replace('\\', '/');
    }
}
