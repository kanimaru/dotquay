using Dotquay.Core.Manifest;

namespace Dotquay.Core.Resolve;

/// <summary>The fully walked and unified dependency graph (SPEC.md §8) — every addon reachable from
/// the project's top-level <c>[addons.*]</c> entries (including the top-level entries themselves),
/// each resolved to exactly one commit, plus every <c>[dotnet.*]</c> dependency declared by any
/// addon in the graph (SPEC.md §7.2/§11, requirement P1-5).</summary>
/// <param name="Warnings">Non-fatal observations from the walk — an addon whose package manifest
/// couldn't be read or sits outside its installed subtree, and whose dependencies were therefore
/// skipped. Carried on the install/update path too, not just <c>dotquay graph</c>, because
/// <c>update</c> is where someone actually notices a dependency failing to appear.</param>
public sealed record GraphResolutionResult(
    IReadOnlyList<GraphAddonEntry> Addons,
    IReadOnlyList<DotnetSpec> AddonDeclaredDotnet,
    IReadOnlyList<GraphWarning> Warnings)
{
    /// <summary>Feeds declared by addons in the graph, awaiting the project's adoption decision
    /// (SPEC.md §11.3). Init-only with a default rather than a positional parameter so every existing
    /// construction site stays valid.</summary>
    public IReadOnlyList<AddonDeclaredSource> AddonDeclaredSources { get; init; } = [];

    /// <summary>Addon name to the <c>[dotnet.*]</c> package IDs it declared — the stakes an adoption
    /// prompt names, and the default owned-ID set when a declared feed lists no
    /// <c>packages</c> patterns.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DotnetPackagesByAddon { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}
