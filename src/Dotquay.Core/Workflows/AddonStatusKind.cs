namespace Dotquay.Core.Workflows;

/// <summary>The three (plus one) outcomes <c>dotquay status</c> reports per addon (SPEC.md §6,
/// requirement P1-4).</summary>
public enum AddonStatusKind {
    /// <summary>On-disk content hash matches the lockfile exactly, and no patch is recorded for it.</summary>
    Clean,

    /// <summary>On-disk content hash matches the lockfile exactly, and a patch <em>is</em> recorded
    /// (SPEC.md §10) — the installed tree is exactly what applying that patch on top of the pristine
    /// upstream copy produces, so this still counts as expected rather than as a problem.</summary>
    PatchedAsExpected,

    /// <summary>On-disk content hash doesn't match the lockfile — a hand-edit exists that isn't
    /// captured as a patch (the same condition <see cref="Sync.DirtyChecker"/> refuses to overwrite
    /// during install/update, requirement P0-6).</summary>
    Modified,

    /// <summary>The addon's folder doesn't exist on disk at all — not one of the three states named
    /// in SPEC.md's command table, but a real and common situation (e.g. before the first
    /// <c>install</c>, or after a manual delete) worth reporting distinctly rather than silently as
    /// "clean" or crashing while trying to hash a directory that isn't there.</summary>
    Missing,

    /// <summary>The addon's folder is a directory link into a folder on this machine (SPEC.md §7.4),
    /// so its contents are somebody's live working copy. Reported as its own state rather than
    /// squeezed into the others: it is neither clean (the content isn't what the lockfile pinned) nor
    /// modified (nothing is wrong, and there is nothing to capture as a patch).</summary>
    Linked,
}
