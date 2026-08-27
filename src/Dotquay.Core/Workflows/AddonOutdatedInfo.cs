namespace Dotquay.Core.Workflows;

/// <summary>One addon's outcome from <see cref="DependencyWorkflow.OutdatedAsync"/> — pairs what's
/// currently locked with what's newest available upstream, without touching disk (SPEC.md §6,
/// requirement P1-3: "runs without cloning"). <see cref="LatestDisplay"/> is null for a
/// <c>rev</c>-pinned addon (nothing to compare against a hard pin) or when the resolver's own check
/// found nothing newer.</summary>
public sealed record AddonOutdatedInfo(string Name, string CurrentDisplay, string? LatestDisplay, bool IsOutdated);
