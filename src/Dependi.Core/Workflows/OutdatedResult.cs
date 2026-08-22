namespace Dependi.Core.Workflows;

/// <summary>What <see cref="DependencyWorkflow.OutdatedAsync"/> found, split by kind (see
/// <see cref="InstallResult"/> for why). Includes every checked entry, not just the outdated ones —
/// callers filter on <see cref="AddonOutdatedInfo.IsOutdated"/>/<see cref="DotnetOutdatedInfo.IsOutdated"/>
/// so "everything is current" is a real, printable answer rather than an empty list indistinguishable
/// from "nothing was checked".</summary>
public sealed record OutdatedResult(IReadOnlyList<AddonOutdatedInfo> Addons, IReadOnlyList<DotnetOutdatedInfo> Dotnet);
