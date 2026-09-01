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

        if (dirty.IsDirty && !force && !MatchesIncomingVersion(stagingPath, destinationPath))
        {
            throw new SyncException(
                $"'{destinationPath}' has local modifications that don't match the lockfile. "
                + "Run 'dotquay patch' to capture them, or pass --force to overwrite (SPEC.md requirement P0-6).");
        }

        // Read before the move, because the move is what destroys them.
        var carried = CollectCarriedArtifacts(stagingPath, destinationPath);

        _fileSystem.MoveDirectory(stagingPath, destinationPath);

        foreach (var (relative, contents) in carried)
        {
            var target = _fileSystem.CombinePath(destinationPath, relative);
            _fileSystem.CreateDirectory(ParentOf(target));
            _fileSystem.WriteAllBytes(target, contents);
        }

        return ContentHasher.HashDirectory(_fileSystem, destinationPath);
    }

    /// <summary>
    /// Whether what is on disk already <em>is</em> the version being installed, ignoring
    /// editor-generated files.
    /// </summary>
    /// <remarks>
    /// The acquittal for a recorded hash that can no longer be reconciled with anything. A lockfile
    /// hash describes the tree as it stood at install time; if the editor has since rewritten an
    /// addon's <c>.import</c> files — and, for an addon that ships its own <c>.uid</c> files, folded
    /// those into the recorded number too — then neither the current nor the legacy hash can match,
    /// on content nobody touched. Comparing numbers cannot resolve that. Comparing trees can.
    /// <para>
    /// This cannot mask a real hand-edit: it only excuses a destination that is byte-identical to what
    /// is about to replace it, and there is by definition nothing to lose in overwriting that. The
    /// fresh hash returned from the swap then re-baselines the lockfile, so each addon self-heals the
    /// first time it is installed.
    /// </para>
    /// <para>
    /// Only reached when the hash comparison already failed, so the ordinary path still hashes the
    /// destination once and never touches staging.
    /// </para>
    /// </remarks>
    private bool MatchesIncomingVersion(string stagingPath, string destinationPath)
    {
        if (!_fileSystem.DirectoryExists(destinationPath) || !_fileSystem.DirectoryExists(stagingPath))
        {
            return false;
        }

        return ContentHasher.HashDirectory(_fileSystem, destinationPath)
            == ContentHasher.HashDirectory(_fileSystem, stagingPath);
    }

    /// <summary>
    /// The editor-generated files that exist at the destination and are <em>not</em> in the incoming
    /// version, so the swap can put them back afterwards.
    /// </summary>
    /// <remarks>
    /// Without this, every update reassigns UIDs. Godot writes a <c>.uid</c> next to each script and a
    /// <c>uid=</c> line into each <c>.import</c>; a wholesale replace deletes them, and Godot then
    /// generates <em>new</em> ones — so every <c>uid://</c> reference into the addon points at
    /// something that no longer exists. Godot's scene format carries a path alongside each uid and
    /// repairs itself on the next save, so this is usually invisible; a bare <c>uid://</c> passed to
    /// <c>load()</c> has no such fallback and simply breaks. <c>doctor</c> reports those.
    /// <para>
    /// An addon that ships its own artifact wins: it is content the author versioned, and the
    /// author's uid is the one the addon's own scenes reference.
    /// </para>
    /// </remarks>
    private List<(string Relative, byte[] Contents)> CollectCarriedArtifacts(string stagingPath, string destinationPath)
    {
        var carried = new List<(string, byte[])>();

        if (!_fileSystem.DirectoryExists(destinationPath))
        {
            return carried;
        }

        foreach (var file in _fileSystem.EnumerateFiles(destinationPath, recursive: true))
        {
            var relative = Relative(file, destinationPath);

            if (!GodotArtifacts.IsArtifact(relative)
                || _fileSystem.FileExists(_fileSystem.CombinePath(stagingPath, relative)))
            {
                continue;
            }

            carried.Add((relative, _fileSystem.ReadAllBytes(file)));
        }

        return carried;
    }

    private static string Relative(string filePath, string root) =>
        filePath[root.Length..].TrimStart('/', '\\').Replace('\\', '/');

    private static string ParentOf(string path)
    {
        var index = path.LastIndexOfAny(['/', '\\']);
        return index <= 0 ? path : path[..index];
    }
}
