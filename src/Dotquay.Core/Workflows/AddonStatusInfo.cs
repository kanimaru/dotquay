namespace Dotquay.Core.Workflows;

/// <summary>One addon's outcome from <see cref="DependencyWorkflow.Status"/>.</summary>
/// <param name="Explanation">Why <paramref name="Kind"/> is
/// <see cref="AddonStatusKind.Modified"/> — populated only by
/// <see cref="DependencyWorkflow.StatusAsync"/> with <c>explain: true</c>, and only for modified
/// addons (docs/DESIGN-v0.12-MODIFIED-REASON.md). Optional and last so the plain, offline
/// <see cref="DependencyWorkflow.Status"/> path and every existing construction site are
/// unchanged.</param>
public sealed record AddonStatusInfo(string Name, AddonStatusKind Kind,
    AddonExplanation? Explanation = null);
