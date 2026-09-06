using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Sync;

/// <summary>
/// Compares two directory trees file by file and classifies every difference
/// (docs/DESIGN-v0.12-MODIFIED-REASON.md). The automated form of the diagnostic SPEC.md §7.3 and
/// <see cref="Dotquay.Core.Doctor.AddonFolderCaseChecker"/> have long described in prose: diff the
/// installed tree against its own locked commit, split artifact from real.
/// </summary>
/// <remarks>
/// Pure and offline — it takes two paths that already exist. Fetching the commit and extracting the
/// subtree that produces the expected side is the caller's job
/// (<see cref="Dotquay.Core.Workflows.DependencyWorkflow"/>), which keeps this type testable against
/// <c>InMemoryFileSystem</c> alone and reusable by anything else that ends up needing a classified
/// diff.
/// </remarks>
public static class TreeComparer
{
    /// <summary>
    /// Every path that differs between <paramref name="expectedRoot"/> and
    /// <paramref name="actualRoot"/>, ordered so the differences that matter come first.
    /// </summary>
    public static IReadOnlyList<TreeChange> Compare(IFileSystem fileSystem, string expectedRoot, string actualRoot)
    {
        var expected = Index(fileSystem, expectedRoot);
        var actual = Index(fileSystem, actualRoot);

        var changes = new List<TreeChange>();

        foreach (var (relative, actualPath) in actual)
        {
            if (!expected.TryGetValue(relative, out var expectedPath))
            {
                changes.Add(new TreeChange(relative, ChangeKind.Added, ChangeClassifier.ClassifyByPath(relative)));
                continue;
            }

            var expectedBytes = fileSystem.ReadAllBytes(expectedPath);
            var actualBytes = fileSystem.ReadAllBytes(actualPath);

            if (!expectedBytes.AsSpan().SequenceEqual(actualBytes))
            {
                changes.Add(new TreeChange(
                    relative, ChangeKind.Changed, ChangeClassifier.Classify(relative, expectedBytes, actualBytes)));
            }
        }

        foreach (var relative in expected.Keys.Where(key => !actual.ContainsKey(key)))
        {
            changes.Add(new TreeChange(relative, ChangeKind.Removed, ChangeClassifier.ClassifyByPath(relative)));
        }

        // Ordered by how much the reader needs to see it, then by path. The caller caps the list it
        // reports, so this is what decides which changes survive the cap — a truncated list that
        // dropped the one real edit in favour of forty artifact files would be worse than no list.
        return changes
            .OrderBy(change => SortPriority(change.Class))
            .ThenBy(change => change.Path, StringComparer.Ordinal)
            .ToList();
    }

    private static int SortPriority(ChangeClass @class) => @class switch
    {
        ChangeClass.Content => 0,
        ChangeClass.UidOnly => 1,
        ChangeClass.LineEndingsOnly => 2,
        ChangeClass.EditorArtifact => 3,
        _ => 4,
    };

    private static Dictionary<string, string> Index(IFileSystem fileSystem, string root)
    {
        if (!fileSystem.DirectoryExists(root))
        {
            return [];
        }

        var index = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in fileSystem.EnumerateFiles(root, recursive: true))
        {
            index[NormalizeRelative(file, root)] = file;
        }

        return index;
    }

    /// <summary>Deliberately a copy of <see cref="ContentHasher"/>'s own normalisation rather than a
    /// shared helper: the hasher decides what "modified" means and is not to be edited for the
    /// convenience of the thing that merely explains it.</summary>
    private static string NormalizeRelative(string filePath, string root)
    {
        var relative = filePath[root.Length..].TrimStart('/', '\\');
        return relative.Replace('\\', '/');
    }
}
