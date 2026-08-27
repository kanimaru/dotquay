namespace Dotquay.Core.Workflows;

/// <summary>What <see cref="DependencyWorkflow.UpdateAsync"/> re-resolved, split by kind (see
/// <see cref="InstallResult"/> for why).</summary>
/// <param name="Warnings">Non-fatal notes from the graph walk (SPEC.md §8) — most usefully, an addon
/// whose own package manifest couldn't be read or wasn't inside its installed subtree, so its
/// dependencies were skipped. Surfaced here because <c>update</c> is the command someone runs when a
/// transitive dependency fails to show up.</param>
public sealed record UpdateResult(
    IReadOnlyList<AddonUpdateResult> Addons,
    IReadOnlyList<DotnetUpdateResult> Dotnet,
    IReadOnlyList<string> Warnings);
