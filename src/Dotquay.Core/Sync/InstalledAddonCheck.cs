using Dotquay.Core.Abstractions;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Resolve;
using Dotquay.Core.Workflows;

namespace Dotquay.Core.Sync;

/// <summary>
/// Decides whether an addon's folder already <em>is</em> the commit being installed, so the install can
/// leave it alone instead of extracting, swapping and re-hashing an identical tree.
/// </summary>
/// <remarks>
/// <para>
/// Measured on vbeami: a no-op <c>update</c> spent about two of its seven seconds copying four
/// unchanged addons out of the cache, deleting the installed copies and moving the fresh ones in.
/// <c>update</c> re-resolves every top-level addon, so the graph sync's own "unchanged" shortcut never
/// applied to them, and <c>install</c> has no shortcut at all.
/// </para>
/// <para>
/// Every condition below is one way the full install would have done something observable, and each
/// one sends the addon down the full path rather than being reasoned around:
/// </para>
/// <list type="bullet">
/// <item>No previous record, or one without a content hash: there is nothing to compare against.</item>
/// <item>A different repository, commit, subtree or folder: that is a real change - the rename and
/// case-correction logic lives on the full path.</item>
/// <item>A captured patch: re-applying it is the full path's job (SPEC.md §10 step 3), and the patch
/// file may have been edited since; this check does not try to tell.</item>
/// <item>A pending conflict marker: <c>patch --resolve</c> owns that addon until it is settled.</item>
/// <item>No folder on disk, or a directory link where the folder should be: the first must be
/// installed, the second must be refused with the full path's explanation (v0.16).</item>
/// <item>A dirty tree: hand-edits must still be refused (P0-6), not silently kept. Only a tree the
/// lockfile's hash accepts counts as in place.</item>
/// </list>
/// <para>
/// What is deliberately <em>not</em> required: editor artifacts matching. <c>.uid</c>/<c>.import</c>
/// files are left out of the hash (<see cref="GodotArtifacts"/>), and leaving Godot's rewritten copies
/// where they are is what the full path's carry-over tries to approximate anyway.
/// </para>
/// </remarks>
public static class InstalledAddonCheck {
    public static bool IsAlreadyInPlace(IFileSystem fileSystem, ProjectLayout layout, ResolvedAddon resolved, LockedAddon? previous) {
        if (previous?.ContentHash is null
            || previous.PatchHash is not null
            || previous.PatchBaseSha is not null) {
            return false;
        }

        if (!string.Equals(previous.Name, resolved.Name, StringComparison.Ordinal)
            || !string.Equals(previous.Git, resolved.Git, StringComparison.Ordinal)
            || !string.Equals(previous.Resolved, resolved.ResolvedSha, StringComparison.Ordinal)
            || !string.Equals(previous.Path, resolved.Path, StringComparison.Ordinal)
            || !string.Equals(previous.Folder, resolved.InstallAs, StringComparison.Ordinal)) {
            return false;
        }

        if (fileSystem.FileExists(layout.ConflictMarkerPath(fileSystem, resolved.Name))) {
            return false;
        }

        var destination = InstallPaths.For(fileSystem, layout.ProjectDirectory, resolved);

        // DirtyChecker calls a missing folder "not dirty" - right for its purpose, wrong for this one.
        if (!fileSystem.DirectoryExists(destination) || fileSystem.ResolveDirectoryLinkTarget(destination) is not null) {
            return false;
        }

        return !DirtyChecker.Check(fileSystem, destination, previous.ContentHash).IsDirty;
    }
}
