using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Workflows;

/// <summary>
/// Clears what earlier runs left in <c>.dotquay/staging/</c>.
/// </summary>
/// <remarks>
/// Staging holds scratch trees that live for one command: an addon extracted before its swap, the
/// graph and preview discovery trees, patch bases. A run that fails or is killed between extracting
/// and cleaning up leaves them behind, and nothing ever came back for them — BotV2 carried a full
/// copy of twitcher there for weeks, after twitcher had become a local link.
/// <para>
/// The one tree that must survive is a pending patch conflict: <c>&lt;name&gt;</c> beside its
/// <c>&lt;name&gt;.conflict</c> marker, which the user edits before <c>patch --resolve</c>.
/// Everything else is safe to delete because dotquay writes to a project one command at a time (the
/// editor plugin queues its CLI calls), so nothing in staging can belong to a run still in flight.
/// </para>
/// </remarks>
public static class StagingSweeper {
    private const string ConflictSuffix = ".conflict";

    public static void Sweep(IFileSystem fileSystem, ProjectLayout layout) {
        var root = layout.StagingRoot(fileSystem);
        if (!fileSystem.DirectoryExists(root)) {
            return;
        }

        var pendingConflicts = fileSystem.EnumerateFiles(root, recursive: false)
            .Select(Name)
            .Where(name => name.EndsWith(ConflictSuffix, StringComparison.Ordinal))
            .Select(name => name[..^ConflictSuffix.Length])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var directory in fileSystem.EnumerateDirectories(root).ToList()) {
            if (!pendingConflicts.Contains(Name(directory))) {
                fileSystem.DeleteDirectory(directory);
            }
        }

        foreach (var file in fileSystem.EnumerateFiles(root, recursive: false).ToList()) {
            if (!Name(file).EndsWith(ConflictSuffix, StringComparison.Ordinal)) {
                fileSystem.DeleteFile(file);
            }
        }
    }

    private static string Name(string path) {
        var trimmed = path.TrimEnd('/', '\\');
        return trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
    }
}
