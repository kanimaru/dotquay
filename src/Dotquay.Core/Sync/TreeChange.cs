namespace Dotquay.Core.Sync;

/// <summary>
/// How the installed tree differs from the expected one at a single path, from the installed tree's
/// point of view: <see cref="Added"/> means the file is on disk and not in the locked commit.
/// </summary>
public enum ChangeKind {
    Added,
    Removed,
    Changed,
}

/// <summary>
/// Whether one difference is something a force-update would destroy, or something that got there on
/// its own and can be discarded without loss (docs/DESIGN-v0.12-MODIFIED-REASON.md).
/// </summary>
/// <remarks>
/// This is a wire enum from the day it ships — <see cref="Dotquay.Core.Json.JsonMapper"/> maps it
/// to fixed strings that the editor plugin and any other <c>--json</c> consumer will switch on.
/// Adding a member is a contract change, under the same rule as
/// <see cref="Dotquay.Core.Workflows.DoctorIssueKind"/>.
/// </remarks>
public enum ChangeClass {
    /// <summary>
    /// A path <see cref="GodotArtifacts.IsArtifact"/> already excludes from the content hash.
    /// </summary>
    /// <remarks>
    /// Can never be the reason an addon reads as modified — those paths are not hashed. Reported
    /// anyway, because the explanation walks the whole tree and "nine artifact files also differ,
    /// and here is why they don't count" is context rather than noise.
    /// </remarks>
    EditorArtifact,

    /// <summary>Byte-different, identical once CRLF/LF is normalised.</summary>
    LineEndingsOnly,

    /// <summary>
    /// Identical once line endings are normalised and every <c>uid://…</c> value is masked.
    /// </summary>
    /// <remarks>
    /// Godot rewrites a stale <c>uid=</c> on an <c>ext_resource</c> line from the <c>path=</c>
    /// beside it, so this churn is both self-inflicted and self-healing. Discarding it loses
    /// nothing the editor will not immediately regenerate.
    /// </remarks>
    UidOnly,

    /// <summary>
    /// Identical once line endings are normalised, uid values are masked, and trailing whitespace
    /// (spaces and tabs at the end of a line, blank lines at the end of the file) is ignored.
    /// </summary>
    /// <remarks>
    /// The signature of an editor or formatter that trims on save. Indentation is deliberately not
    /// ignored: GDScript is indentation-sensitive, so a leading-whitespace change is a real change.
    /// </remarks>
    WhitespaceOnly,

    /// <summary>A real difference — and anything that could not be proven to be one of the
    /// above.</summary>
    Content,
}

/// <summary>One differing path, and what kind of difference it is.</summary>
/// <param name="Path">Relative to the addon folder, forward-slashed — the same normalisation
/// <see cref="ContentHasher"/> hashes under, so the two always name a file the same way.</param>
public sealed record TreeChange(string Path, ChangeKind Kind, ChangeClass Class) {
    /// <summary>
    /// True when discarding this difference costs the user nothing.
    /// </summary>
    /// <remarks>
    /// Deliberately a property of the change rather than a fifth enum member: "benign" is a
    /// question asked of a class, and folding the answer into the classification would make adding
    /// a new class silently change verdicts.
    /// </remarks>
    public bool IsBenign => Class is not ChangeClass.Content;
}
