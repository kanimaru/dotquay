namespace Dependi.Core.Workflows;

/// <summary>One addon's outcome from <see cref="DependencyWorkflow.Status"/>.</summary>
public sealed record AddonStatusInfo(string Name, AddonStatusKind Kind);
