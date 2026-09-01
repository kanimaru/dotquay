using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Sync;

/// <summary>
/// SPEC.md §9 step 4 / requirement P0-6: before install/update touches an installed addon, verify
/// the on-disk content still matches what the lockfile recorded. A mismatch means hand-edits exist
/// that aren't captured as a patch — refuse rather than silently destroy them.
/// </summary>
public static class DirtyChecker
{
    public sealed record Result(bool IsDirty, string? ActualContentHash);

    public static Result Check(IFileSystem fileSystem, string installedPath, string? expectedContentHash)
    {
        if (!fileSystem.DirectoryExists(installedPath))
        {
            // Nothing installed yet — not "dirty", just absent. The caller decides what an absent
            // destination means for the operation it's doing.
            return new Result(IsDirty: false, ActualContentHash: null);
        }

        var actual = ContentHasher.HashDirectory(fileSystem, installedPath);

        if (expectedContentHash is null)
        {
            // Something already occupies the destination but the lockfile has no prior record for
            // it (e.g. a same-named folder that predates dotquay managing it) — treat as dirty so
            // sync never silently clobbers content it didn't put there.
            return new Result(IsDirty: true, ActualContentHash: actual);
        }

        if (actual == expectedContentHash)
        {
            return new Result(IsDirty: false, ActualContentHash: actual);
        }

        // Only reached on a mismatch, so the common path still hashes the tree once. A lockfile
        // written before v0.9 recorded a hash that included editor-generated files; for an addon
        // that ships its own .uid files, that is a different number for identical content. Matching
        // the old form counts as clean, and the caller records `actual` — the current form — so each
        // addon migrates the first time it is installed or updated.
        var legacy = ContentHasher.HashDirectoryIncludingArtifacts(fileSystem, installedPath);

        return new Result(IsDirty: legacy != expectedContentHash, ActualContentHash: actual);
    }
}
