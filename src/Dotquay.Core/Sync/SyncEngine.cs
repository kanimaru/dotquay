using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Sync;

/// <summary>
/// Owns the one step in the pipeline allowed to touch <c>addons/&lt;name&gt;</c>: the
/// staging-to-destination swap (SPEC.md §9 step 5). Subtree extraction into staging happens
/// upstream (<see cref="SubtreeExtractor"/>) — everything up to that point can fail freely without
/// the project folder ever seeing a half-finished update.
/// </summary>
public sealed class SyncEngine
{
    private readonly IFileSystem _fileSystem;

    public SyncEngine(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Swaps <paramref name="stagingPath"/> into <paramref name="destinationPath"/> and returns the
    /// installed tree's content hash (to record in the lockfile). Refuses when the destination has
    /// content that doesn't match <paramref name="expectedContentHash"/>, unless
    /// <paramref name="force"/> is set (requirement P0-6) — dotquay never overwrites hand-edits it
    /// doesn't know about.
    /// </summary>
    public string SwapAndHash(string stagingPath, string destinationPath, string? expectedContentHash, bool force)
    {
        var dirty = DirtyChecker.Check(_fileSystem, destinationPath, expectedContentHash);

        if (dirty.IsDirty && !force)
        {
            throw new SyncException(
                $"'{destinationPath}' has local modifications that don't match the lockfile. "
                + "Run 'dotquay patch' to capture them, or pass --force to overwrite (SPEC.md requirement P0-6).");
        }

        _fileSystem.MoveDirectory(stagingPath, destinationPath);

        return ContentHasher.HashDirectory(_fileSystem, destinationPath);
    }
}
