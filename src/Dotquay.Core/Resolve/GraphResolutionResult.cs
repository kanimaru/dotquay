using Dotquay.Core.Manifest;

namespace Dotquay.Core.Resolve;

/// <summary>The fully walked and unified dependency graph (SPEC.md §8) — every addon reachable from
/// the project's top-level <c>[addons.*]</c> entries (including the top-level entries themselves),
/// each resolved to exactly one commit, plus every <c>[dotnet.*]</c> dependency declared by any
/// addon in the graph (SPEC.md §7.2/§11, requirement P1-5).</summary>
public sealed record GraphResolutionResult(
    IReadOnlyList<GraphAddonEntry> Addons,
    IReadOnlyList<DotnetSpec> AddonDeclaredDotnet);
