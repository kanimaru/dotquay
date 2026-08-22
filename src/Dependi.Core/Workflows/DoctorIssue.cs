namespace Dependi.Core.Workflows;

/// <summary>One finding from <see cref="DependencyWorkflow.DoctorAsync"/>. <see cref="Message"/> is
/// already a complete, human-readable sentence — the CLI just prints it, no further formatting by
/// <see cref="Kind"/> needed (unlike <see cref="AddonStatusInfo"/>/<see cref="AddonStatusKind"/>,
/// where the CLI picks the color and wording; doctor's issues are heterogeneous enough that
/// per-kind templates would just duplicate what's already in the message).</summary>
public sealed record DoctorIssue(DoctorIssueKind Kind, string Message);
