namespace Dotquay.Core.Workflows;

/// <summary>What <see cref="DependencyWorkflow.UpdateAsync"/> re-resolved, split by kind (see
/// <see cref="InstallResult"/> for why).</summary>
public sealed record UpdateResult(IReadOnlyList<AddonUpdateResult> Addons, IReadOnlyList<DotnetUpdateResult> Dotnet);
