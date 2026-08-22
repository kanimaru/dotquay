namespace Dependi.Core.Resolve;

/// <summary>One addon in a fully unified dependency graph (SPEC.md §8) — the graph resolver's
/// per-addon output. <see cref="DependencyNames"/> are the direct child addon names this addon's own
/// package manifest declared (SPEC.md §7.2), recorded on <see cref="Lockfile.LockedAddon.Dependencies"/>
/// as documentation of the graph shape; <c>install</c> never walks them (it replays the already-
/// flattened lockfile), so this is informational, not load-bearing for reproducibility.</summary>
public sealed record GraphAddonEntry(ResolvedAddon Resolved, IReadOnlyList<string> DependencyNames);
