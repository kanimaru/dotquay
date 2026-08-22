namespace Dependi.Core.Workflows;

/// <summary>What <see cref="DependencyWorkflow.DoctorAsync"/> found. Empty <see cref="Issues"/> means
/// every check passed — <see cref="IsHealthy"/> spares the CLI from re-deriving that from a count.</summary>
public sealed record DoctorResult(IReadOnlyList<DoctorIssue> Issues)
{
    public bool IsHealthy => Issues.Count == 0;
}
