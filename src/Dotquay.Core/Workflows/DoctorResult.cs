namespace Dotquay.Core.Workflows;

/// <summary>What <see cref="DependencyWorkflow.DoctorAsync"/> found. Empty <see cref="Issues"/> means
/// every check passed — <see cref="IsHealthy"/> spares the CLI from re-deriving that from a count.</summary>
public sealed record DoctorResult(IReadOnlyList<DoctorIssue> Issues) {
    public bool IsHealthy => Issues.Count == 0;

    /// <summary>What the project trusts and what that trust brought in (SPEC.md §11.3). Reported for
    /// visibility, never counted as a fault — see <see cref="DoctorTrust"/> for why the distinction
    /// matters.</summary>
    public DoctorTrust Trust { get; init; } = DoctorTrust.Empty;
}
