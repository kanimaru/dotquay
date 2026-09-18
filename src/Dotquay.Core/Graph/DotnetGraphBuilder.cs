using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;

namespace Dotquay.Core.Graph;

/// <summary>
/// Builds the NuGet half of a <see cref="GraphReport"/>: one <see cref="GraphDotnetNode"/> per
/// package, with who declared it (DESIGN-v0.19 §3).
/// </summary>
/// <remarks>
/// <para>
/// A pure function of plain data, separate from <see cref="GraphInspector"/>, because the two graph
/// modes get their inputs from different places but must agree on the output. Lockfile mode reads
/// installed package manifests off disk; resolved mode uses the live walk's
/// <see cref="GraphAnalysis.DotnetPackagesByAddon"/>. If each mode worked out provenance on its own,
/// the panel would show a different answer depending on whether the user had pressed Re-resolve.
/// </para>
/// <para>
/// The precedence mirrors the installer's (requirement P1-5): the project's own <c>[dotnet.*]</c>
/// entry always wins, and its constraint is the one shown. Addons that also declare the package
/// are still listed in <see cref="GraphDotnetNode.RequiredBy"/>, because that is exactly the edge
/// the graph should draw.
/// </para>
/// </remarks>
public static class DotnetGraphBuilder
{
    /// <param name="manifest">The project manifest. Its <c>[dotnet.*]</c> keys are the roots.</param>
    /// <param name="lockfile">Supplies <c>resolved</c> and <c>source</c>, and the leftovers that
    /// become orphans.</param>
    /// <param name="addonDeclared">The winning spec per addon-declared package, used for the
    /// constraint shown on a transitive package. One entry per name; extra entries are ignored
    /// after the first.</param>
    /// <param name="packagesByAddon">Addon name to the package IDs it declares. Only addons the caller
    /// considers live (reachable, or reached by the walk) belong in here.</param>
    public static IReadOnlyList<GraphDotnetNode> Build(
        ProjectManifest manifest,
        DotquayLockfile lockfile,
        IReadOnlyList<DotnetSpec> addonDeclared,
        IReadOnlyDictionary<string, IReadOnlyList<string>> packagesByAddon)
    {
        var lockedByName = new Dictionary<string, LockedDotnet>(StringComparer.Ordinal);
        foreach (var locked in lockfile.Dotnet)
        {
            lockedByName.TryAdd(locked.Name, locked);
        }

        var addonRequirers = InvertByPackage(packagesByAddon);
        var result = new List<GraphDotnetNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, spec) in manifest.Dotnet)
        {
            seen.Add(name);
            lockedByName.TryGetValue(name, out var locked);
            result.Add(new GraphDotnetNode(name, spec.Version, locked?.Resolved, DeclaredByProject: true)
            {
                Origin = GraphNodeOrigin.Root,
                RequiredBy = [GraphConflict.ProjectRequirerName, .. RequirersOf(name, addonRequirers)],
                Source = locked?.Source,
            });
        }

        foreach (var spec in addonDeclared.Where(s => seen.Add(s.Name)))
        {
            lockedByName.TryGetValue(spec.Name, out var locked);
            result.Add(new GraphDotnetNode(spec.Name, spec.Version, locked?.Resolved, DeclaredByProject: false)
            {
                Origin = GraphNodeOrigin.Transitive,
                RequiredBy = RequirersOf(spec.Name, addonRequirers),
                Source = locked?.Source,
            });
        }

        // Locked, but neither the project nor any live addon declares it any more. This is the NuGet
        // version of an orphan addon, and the same drift doctor reports as a dangling entry.
        foreach (var locked in lockfile.Dotnet.Where(d => seen.Add(d.Name)))
        {
            result.Add(new GraphDotnetNode(locked.Name, null, locked.Resolved, DeclaredByProject: false)
            {
                Origin = GraphNodeOrigin.Orphan,
                RequiredBy = [],
                Source = locked.Source,
            });
        }

        return result;
    }

    /// <summary>
    /// Flattens per-addon declarations (lockfile mode's input) into the winner list and the
    /// addon-to-names index that <see cref="Build"/> takes.
    /// </summary>
    /// <remarks>
    /// Addons are visited in ordinal name order, so the constraint shown for a package two addons
    /// declare differently is stable across refreshes. The live walk picks its winner by BFS discovery
    /// order instead, which lockfile mode can't reproduce without the walk. The difference only shows
    /// in the constraint text; <c>resolved</c> always comes from the lockfile, so the version the
    /// user actually has is never affected.
    /// </remarks>
    public static (IReadOnlyList<DotnetSpec> Winners, IReadOnlyDictionary<string, IReadOnlyList<string>> PackagesByAddon)
        Flatten(IReadOnlyDictionary<string, IReadOnlyList<DotnetSpec>> declarationsByAddon)
    {
        var winners = new Dictionary<string, DotnetSpec>(StringComparer.Ordinal);
        var packagesByAddon = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var addon in declarationsByAddon.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            var specs = declarationsByAddon[addon];
            packagesByAddon[addon] = specs.Select(s => s.Name).ToList();

            foreach (var spec in specs)
            {
                winners.TryAdd(spec.Name, spec);
            }
        }

        return (winners.Values.ToList(), packagesByAddon);
    }

    private static Dictionary<string, SortedSet<string>> InvertByPackage(
        IReadOnlyDictionary<string, IReadOnlyList<string>> packagesByAddon)
    {
        var result = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var (addon, packages) in packagesByAddon)
        {
            foreach (var package in packages)
            {
                if (!result.TryGetValue(package, out var addons))
                {
                    addons = new SortedSet<string>(StringComparer.Ordinal);
                    result[package] = addons;
                }

                addons.Add(addon);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> RequirersOf(string package, Dictionary<string, SortedSet<string>> addonRequirers) =>
        addonRequirers.TryGetValue(package, out var addons) ? addons.ToList() : [];
}
