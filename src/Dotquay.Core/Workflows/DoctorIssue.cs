namespace Dotquay.Core.Workflows;

/// <summary>One finding from <see cref="DependencyWorkflow.DoctorAsync"/>. <see cref="Message"/> is
/// already a complete, human-readable sentence — the CLI just prints it, no further formatting by
/// <see cref="Kind"/> needed (unlike <see cref="AddonStatusInfo"/>/<see cref="AddonStatusKind"/>,
/// where the CLI picks the color and wording; doctor's issues are heterogeneous enough that
/// per-kind templates would just duplicate what's already in the message).</summary>
public sealed record DoctorIssue(DoctorIssueKind Kind, string Message, UidReferenceFix? UidFix = null);

/// <summary>The three facts <see cref="DoctorIssueKind.FragileUidReference"/> already knows about its
/// own fix: which script named the uid, the literal token it wrote, and the res:// path that same
/// token resolves to right now.</summary>
/// <remarks>
/// Carried as data, not folded back out of <see cref="DoctorIssue.Message"/> by every consumer with
/// its own guess at the sentence shape. <see cref="Doctor.UidReferenceScanner"/> already computes all
/// three before the message is even built; this just keeps them instead of throwing them away.
/// <see langword="null"/> for every other <see cref="DoctorIssueKind"/>.
/// </remarks>
public sealed record UidReferenceFix(string File, string Uid, string ResPath);
