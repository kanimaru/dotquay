using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Sync;

/// <summary>
/// Extracts an addon's <c>path</c> subtree out of its fetched repo into a staging folder — solves
/// the "addon is not at repo root" problem without submodules (SPEC.md §9 step 2). Binary-safe:
/// copies bytes, never round-trips file contents through text encoding.
/// </summary>
public static class SubtreeExtractor
{
    /// <summary>Never copied into staging — git's own metadata and dotquay's cache bookkeeping,
    /// not part of the addon itself.</summary>
    private static readonly string[] ExcludedTopLevelSegments = [".git", ".dotquay-fetch-complete"];

    /// <summary>
    /// Returns <paramref name="relativeSubtreePath"/> with each segment replaced by the repository's
    /// actual casing, or unchanged when any segment has no case-insensitive match.
    /// </summary>
    /// <remarks>
    /// dotquay guesses <c>addons/&lt;name&gt;</c> from the repo's URL, and a repo whose folder is
    /// cased differently from its own name defeats that guess silently. Gut is the worked example:
    /// the repository is <c>bitwes/Gut</c> but its subtree is <c>addons/gut</c>, so the guess became
    /// <c>addons/Gut</c>. On Windows that <em>extracts</em>, because the filesystem is
    /// case-insensitive — but Godot's <c>res://</c> paths are case-sensitive strings, so the addon
    /// installed to <c>addons/Gut</c> while its own 88 files still said
    /// <c>res://addons/gut/…</c>. Godot then repairs every one of those references and re-saves the
    /// files, which reads as an addon that is permanently hand-modified. On Linux the same guess
    /// fails outright.
    /// <para>
    /// This corrects <em>case</em> and nothing else. A segment that doesn't match case-insensitively
    /// is left exactly as configured, so the existing "path does not exist in the fetched repository"
    /// error still fires and dotquay still never infers a layout it wasn't told (SPEC.md §7.1, OQ-2).
    /// </para>
    /// </remarks>
    public static string ResolveSubtreePath(IFileSystem fileSystem, string cachedRepoPath, string relativeSubtreePath)
    {
        if (relativeSubtreePath is "." or "")
        {
            return relativeSubtreePath;
        }

        var current = cachedRepoPath;
        var resolved = new List<string>();

        foreach (var segment in relativeSubtreePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var actual = ActualDirectoryName(fileSystem, current, segment);
            if (actual is null)
            {
                return relativeSubtreePath;
            }

            resolved.Add(actual);
            current = fileSystem.CombinePath(current, actual);
        }

        return string.Join('/', resolved);
    }

    /// <summary>The real name of <paramref name="segment"/> inside <paramref name="parent"/>, or null
    /// when nothing there matches it even case-insensitively.</summary>
    private static string? ActualDirectoryName(IFileSystem fileSystem, string parent, string segment)
    {
        if (!fileSystem.DirectoryExists(parent))
        {
            return null;
        }

        foreach (var directory in fileSystem.EnumerateDirectories(parent))
        {
            var name = directory.TrimEnd('/', '\\');
            var separator = name.LastIndexOfAny(['/', '\\']);
            if (separator >= 0)
            {
                name = name[(separator + 1)..];
            }

            if (string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

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
