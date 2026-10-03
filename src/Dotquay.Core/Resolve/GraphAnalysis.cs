using Dotquay.Core.Manifest;

namespace Dotquay.Core.Resolve;

/// <summary>
/// The full, non-throwing picture of one dependency-graph walk (SPEC.md §8) — everything
/// <see cref="GraphResolutionResult"/> carries, plus the names that could <em>not</em> be unified and
/// every requirer's demand on every name. This is what <c>dotquay graph</c> serializes and what the
/// Godot editor plugin draws (SPEC.md §18).
/// </summary>
/// <param name="Addons">Names that unified to exactly one commit.</param>
/// <param name="AddonDeclaredDotnet">Every <c>[dotnet.*]</c> entry declared by any addon in the graph
/// (SPEC.md §7.2/§11, requirement P1-5).</param>
/// <param name="Conflicts">Names that did not unify. A name appears here <em>or</em> in
/// <paramref name="Addons"/>, never both.</param>
/// <param name="RequirementsByName">Who asked for what, for every name the walk reached — including
/// the ones that unified cleanly, so a UI can show provenance ("core 1.4.2, required by a and b")
/// rather than only explaining itself when something breaks.</param>
/// <param name="Warnings">Non-fatal observations that changed nothing about resolution but that a
/// human almost certainly wants to know — chiefly an addon whose package manifest is unreadable or
/// in the wrong folder, and whose dependencies were therefore skipped.</param>
public sealed record GraphAnalysis(
    IReadOnlyList<GraphAddonEntry> Addons,
    IReadOnlyList<DotnetSpec> AddonDeclaredDotnet,
    IReadOnlyList<GraphConflict> Conflicts,
    IReadOnlyDictionary<string, IReadOnlyList<GraphRequirement>> RequirementsByName,
    IReadOnlyList<GraphWarning> Warnings)
{
    /// <summary>Feeds declared by addons in the graph (SPEC.md §11.3). Init-only with a default
    /// rather than a positional parameter, so existing construction sites stay valid.</summary>
    public IReadOnlyList<AddonDeclaredSource> AddonDeclaredSources { get; init; } = [];

    /// <summary>Addon name to the <c>[dotnet.*]</c> package IDs it declared.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DotnetPackagesByAddon { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>Package ID to every addon's range on it, in discovery order (DESIGN-v0.21 §3.5).
    /// <see cref="AddonDeclaredDotnet"/> keeps only the first declaration, which still decides asset
    /// metadata; versions are decided by all of them.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AddonDotnetRequirement>> AddonDotnetRequirements { get; init; } =
        new Dictionary<string, IReadOnlyList<AddonDotnetRequirement>>(StringComparer.Ordinal);
}
