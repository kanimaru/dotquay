using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Core.Workflows;

namespace Dotquay.Core.Graph;

/// <summary>How a <see cref="GraphReport"/> was produced — which decides what it can and cannot
/// tell you.</summary>
public enum GraphSource
{
    /// <summary>Built purely from <c>dotquay.lock</c> plus the manifest and disk. No network, no
    /// resolution: instant, always available, and therefore what the editor plugin opens with. Can
    /// never report a conflict, because the lockfile only exists once conflicts were already
    /// settled.</summary>
    Lockfile,

    /// <summary>Built by re-walking the live dependency graph (SPEC.md §8) against real git refs.
    /// Slower and network-bound, but the only mode that can surface an unresolved
    /// <see cref="GraphConflict"/> and the candidate versions that would settle it.</summary>
    Resolved,
}

/// <summary>Where an addon node came from, which is what a UI needs to decide whether removing it
/// is even meaningful.</summary>
public enum GraphNodeOrigin
{
    /// <summary>Declared in the project's own <c>[addons.*]</c> — the user put it there.</summary>
    Root,

    /// <summary>Pulled in only by another addon's package manifest (SPEC.md §7.2).</summary>
    Transitive,

    /// <summary>Locked and on disk, but no longer declared or reachable — the drift
    /// <c>dotquay doctor</c> flags as a dangling entry.</summary>
    Orphan,
}

/// <summary>One addon in the report. <see cref="Requirements"/> is populated in
/// <see cref="GraphSource.Resolved"/> mode only; lockfile mode knows the graph's shape (from
/// <c>dependencies</c>) but not the constraint text each requirer used.</summary>
public sealed record GraphNode
{
    public required string Name { get; init; }

    public required string Git { get; init; }

    /// <summary>Display version — a tag like <c>"v2.46.1"</c>, or a pseudo-version for untagged
    /// repos (SPEC.md §6). Null for a plain <c>rev</c>/<c>branch</c> pin.</summary>
    public string? Version { get; init; }

    /// <summary>Full commit SHA, or null when a <see cref="GraphSource.Resolved"/> walk couldn't pin
    /// this name at all.</summary>
    public string? ResolvedSha { get; init; }

    /// <summary>The subtree extracted from the addon's repo (SPEC.md §7.1) — where it lives
    /// upstream, not where it lands here. See <see cref="Folder"/>.</summary>
    public required string Path { get; init; }

    /// <summary>The folder under <c>addons/</c> this addon installs into — its name unless
    /// <c>install_as</c> says otherwise (SPEC.md §7.1). Worth showing in a UI whenever it differs
    /// from the name, since that is exactly when "where did this go?" gets asked.</summary>
    public required string Folder { get; init; }

    public required GraphNodeOrigin Origin { get; init; }

    /// <summary>The source kind of the manifest entry that declares this addon, if any — null for a
    /// transitive addon the project never named itself.</summary>
    public AddonSourceKind? SourceKind { get; init; }

    /// <summary>The declared constraint text (<c>"~2.44"</c>, a branch name, or a SHA), paired with
    /// <see cref="SourceKind"/>.</summary>
    public string? Constraint { get; init; }

    /// <summary>True when an <c>[overrides.*]</c> entry decides this name (SPEC.md §8). The editor's
    /// conflict picker writes exactly this, so it also means "the user forced this one".</summary>
    public bool IsOverridden { get; init; }

    public bool IsPatched { get; init; }

    /// <summary>Null when nothing is locked for this name yet (a resolved-mode node the lockfile has
    /// never seen), since there is no recorded content hash to compare disk against.</summary>
    public AddonStatusKind? Status { get; init; }

    /// <summary>Direct child addon names (SPEC.md §8 graph edges).</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>Who required this name and with what constraint. Empty in
    /// <see cref="GraphSource.Lockfile"/> mode.</summary>
    public IReadOnlyList<GraphRequirement> Requirements { get; init; } = [];
}

/// <summary>A directed edge for drawing. <see cref="From"/> is either
/// <see cref="GraphConflict.ProjectRequirerName"/> or an addon name.</summary>
public sealed record GraphEdge(string From, string To);

/// <summary>A NuGet dependency in the report (SPEC.md §11). <see cref="DeclaredByProject"/>
/// distinguishes the project's own <c>[dotnet.*]</c> entries from ones unioned in by an addon
/// (requirement P1-5) — the latter can't be removed from the project manifest, because they aren't
/// in it.</summary>
public sealed record GraphDotnetNode(string Name, string? Constraint, string? Resolved, bool DeclaredByProject);

/// <summary>
/// Everything the Godot editor plugin needs to draw a project's dependency state in one payload
/// (SPEC.md §18): the addon graph, its NuGet side, and any unresolved conflicts with the concrete
/// versions on offer to settle them.
/// </summary>
public sealed record GraphReport
{
    public required GraphSource Source { get; init; }

    public required string ProjectDirectory { get; init; }

    /// <summary>The project's own <c>[addons.*]</c> keys, in manifest order — the graph's roots.</summary>
    public IReadOnlyList<string> RootNames { get; init; } = [];

    public IReadOnlyList<GraphNode> Addons { get; init; } = [];

    public IReadOnlyList<GraphEdge> Edges { get; init; } = [];

    public IReadOnlyList<GraphDotnetNode> Dotnet { get; init; } = [];

    /// <summary>Always empty in <see cref="GraphSource.Lockfile"/> mode.</summary>
    public IReadOnlyList<GraphConflict> Conflicts { get; init; } = [];

    /// <summary>Non-fatal observations from the walk (SPEC.md §8) — an addon whose package manifest
    /// couldn't be read or sits outside its subtree, for instance. Also
    /// <see cref="GraphSource.Resolved"/>-only: the lockfile records what was decided, not what was
    /// noticed while deciding it.</summary>
    public IReadOnlyList<GraphWarning> Warnings { get; init; } = [];

    /// <summary>Names with an <c>[overrides.*]</c> entry in the project manifest, whether or not the
    /// override is currently doing anything.</summary>
    public IReadOnlyList<string> Overrides { get; init; } = [];
}
