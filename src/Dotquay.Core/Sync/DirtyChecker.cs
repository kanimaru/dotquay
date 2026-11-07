using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Sync;

/// <summary>
/// SPEC.md §9 step 4 / requirement P0-6: before install/update touches an installed addon, verify
/// the on-disk content still matches what the lockfile recorded. A mismatch means hand-edits exist
/// that aren't captured as a patch — refuse rather than silently destroy them.
/// </summary>
public static class DirtyChecker {
    public sealed record Result(bool IsDirty, string? ActualContentHash);

    public static Result Check(IFileSystem fileSystem, string installedPath, string? expectedContentHash) {
        if (!fileSystem.DirectoryExists(installedPath)) {
            // Nothing installed yet — not "dirty", just absent. The caller decides what an absent
            // destination means for the operation it's doing.
            return new Result(IsDirty: false, ActualContentHash: null);
        }

        var tree = ContentHasher.HashTree(fileSystem, installedPath);

        if (expectedContentHash is null) {
            // Something already occupies the destination but the lockfile has no prior record for
            // it (e.g. a same-named folder that predates dotquay managing it) — treat as dirty so
            // sync never silently clobbers content it didn't put there.
            return new Result(IsDirty: true, ActualContentHash: tree.Canonical);
        }

        // Clean when the recorded hash is ANY form this tree can have, not only the current one:
        //  - v0.9: lockfiles written before editor-generated files were left out of the hash include
        //    them, which for an addon that ships its own .uid files is a different number for
        //    identical content;
        //  - v0.23: lockfiles hold the hash of the files as git wrote them on whichever machine
        //    installed last, which is CRLF on Windows and LF elsewhere for the same commit. A
        //    teammate's git configuration is not a hand-edit.
        // Anything matching counts as unedited, and a (re)install keeps the recorded value rather
        // than churning it (see SyncEngine.SwapAndHash).
        return new Result(
            IsDirty: !tree.Accepted.Contains(expectedContentHash, StringComparer.Ordinal),
            ActualContentHash: tree.Canonical);
    }
}
