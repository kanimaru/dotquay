using Dotquay.Core.Abstractions;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;

namespace Dotquay.Core.Graph;

/// <summary>
/// Builds a <see cref="GraphReport"/> — the read-only, whole-project view behind <c>dotquay graph</c>
/// and the Godot editor plugin (SPEC.md §18). Strictly read-only: it never writes the manifest, the
/// lockfile, or <c>addons/</c>. Mutations stay with <see cref="DependencyWorkflow"/> and
/// <see cref="OverrideWorkflow"/>, so a panel that only looks at a project can never change it.
/// </summary>
public sealed class GraphInspector
{
    private readonly IFileSystem _fileSystem;
    private readonly IGitClient _gitClient;
    private readonly RepoCache _repoCache;
    private readonly ProjectFileStore _projectFiles;

    public GraphInspector(IFileSystem fileSystem, IGitClient gitClient, string cacheRoot)
        : this(fileSystem, gitClient, new RepoCache(fileSystem, gitClient, cacheRoot))
    {
    }

    public GraphInspector(IFileSystem fileSystem, IGitClient gitClient, RepoCache repoCache)
    {
        _fileSystem = fileSystem;
        _gitClient = gitClient;
        _repoCache = repoCache;
        _projectFiles = new ProjectFileStore(fileSystem);
    }

    /// <summary>
    /// The fast path: reconstructs the graph from <c>dotquay.lock</c>'s recorded
    /// <see cref="LockedAddon.Dependencies"/> edges plus the manifest and disk. No network at all —
    /// this is exactly the property SPEC.md §8 introduced those edges for ("install replays the graph
    /// without network access to manifests"), applied to drawing it. The editor plugin opens with
    /// this so the panel is never waiting on git.
    /// </summary>
    public GraphReport Inspect(string projectDirectory)
    {
        var (manifest, _) = _projectFiles.ReadManifestOrThrow(projectDirectory);
        var (lockfile, _) = _projectFiles.ReadOrCreateLockfile(projectDirectory);

        var reachable = ReachableNames(lockfile, manifest.Addons.Keys);

        var nodes = lockfile.Addons
            .Select(locked => new GraphNode
            {
                Name = locked.Name,
                Git = locked.Git,
                Version = locked.Version,
                ResolvedSha = locked.Resolved,
                Path = locked.Path,
                Folder = locked.EffectiveFolder,
                Origin = OriginOf(locked.Name, manifest, reachable),
                SourceKind = SpecFor(locked.Name, manifest)?.SourceKind,
                Constraint = ConstraintFor(locked.Name, manifest),
                IsOverridden = manifest.Overrides.ContainsKey(locked.Name),
                IsPatched = locked.PatchHash is not null,
                Status = AddonStatusEvaluator.Evaluate(_fileSystem, projectDirectory, locked),
                Dependencies = locked.Dependencies,
            })
            .ToList();

        // A manifest entry that was never locked (hand-added, or added while offline) still belongs
        // on the graph — as a node with no SHA. Silently omitting it would make the panel disagree
        // with the file the user is looking at.
        var lockedNames = lockfile.Addons.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, spec) in manifest.Addons.Where(kv => !lockedNames.Contains(kv.Key)))
        {
            nodes.Add(new GraphNode
            {
                Name = name,
                Git = spec.Git,
                Version = null,
                ResolvedSha = null,
                Path = spec.EffectivePath,
                Folder = spec.EffectiveFolder,
                Origin = GraphNodeOrigin.Root,
                SourceKind = spec.SourceKind,
                Constraint = AddonRequirement.ConstraintText(spec),
                IsOverridden = manifest.Overrides.ContainsKey(name),
                IsPatched = false,
                Status = null,
                Dependencies = [],
            });
        }

        return new GraphReport
        {
            Source = GraphSource.Lockfile,
            ProjectDirectory = projectDirectory,
            RootNames = manifest.Addons.Keys.ToList(),
            Addons = nodes,
            Edges = BuildEdges(manifest.Addons.Keys, nodes),
            Dotnet = BuildDotnet(manifest, lockfile, addonDeclared: []),
            Conflicts = [],
            Overrides = manifest.Overrides.Keys.ToList(),
        };
    }

    /// <summary>
    /// The slow path: re-walks the live graph (SPEC.md §8) so the report can name conflicts and the
    /// candidate versions that would settle them. Still read-only — resolution here is a question, not
    /// a commitment; nothing is installed and no lockfile is written, so a user can inspect a conflict
    /// without the project changing underneath them.
    /// </summary>
    public async Task<GraphReport> InspectResolvedAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        var (manifest, _) = _projectFiles.ReadManifestOrThrow(projectDirectory);
        var (lockfile, _) = _projectFiles.ReadOrCreateLockfile(projectDirectory);
        var lockedByName = lockfile.Addons.ToDictionary(a => a.Name, StringComparer.Ordinal);

        var discoveryRoot = _fileSystem.CombinePath(projectDirectory, ".dotquay", "staging", "graph-inspect");
        var resolver = new DependencyGraphResolver(_fileSystem, _gitClient, _repoCache, discoveryRoot);
        var analysis = await resolver.AnalyzeGraphAsync(manifest.Addons, manifest.Overrides, cancellationToken);

        var nodes = new List<GraphNode>();
        foreach (var entry in analysis.Addons)
        {
            lockedByName.TryGetValue(entry.Resolved.Name, out var locked);
            nodes.Add(new GraphNode
            {
                Name = entry.Resolved.Name,
                Git = entry.Resolved.Git,
                Version = entry.Resolved.DisplayVersion,
                ResolvedSha = entry.Resolved.ResolvedSha,
                Path = entry.Resolved.Path,
                Folder = entry.Resolved.EffectiveFolder,
                Origin = manifest.Addons.ContainsKey(entry.Resolved.Name) ? GraphNodeOrigin.Root : GraphNodeOrigin.Transitive,
                SourceKind = SpecFor(entry.Resolved.Name, manifest)?.SourceKind,
                Constraint = ConstraintFor(entry.Resolved.Name, manifest),
                IsOverridden = manifest.Overrides.ContainsKey(entry.Resolved.Name),
                IsPatched = locked?.PatchHash is not null,
                // Only meaningful against a lockfile entry: without a recorded content hash there is
                // nothing to call the tree on disk clean or dirty *relative to*.
                Status = locked is null ? null : AddonStatusEvaluator.Evaluate(_fileSystem, projectDirectory, locked),
                Dependencies = entry.DependencyNames,
                Requirements = RequirementsFor(entry.Resolved.Name, analysis),
            });
        }

        // A conflicted name has no commit to show, but it must still appear as a node: it is the one
        // the user needs to click on.
        foreach (var conflict in analysis.Conflicts)
        {
            lockedByName.TryGetValue(conflict.AddonName, out var locked);
            nodes.Add(new GraphNode
            {
                Name = conflict.AddonName,
                Git = conflict.Requirements.Count > 0 ? conflict.Requirements[0].Git : locked?.Git ?? "",
                Version = null,
                ResolvedSha = null,
                Path = locked?.Path ?? $"addons/{conflict.AddonName}",
                // A folder conflict is precisely a disagreement about this value, so there is no
                // single answer to report — fall back to what is installed, or the name.
                Folder = locked?.EffectiveFolder ?? conflict.AddonName,
                Origin = manifest.Addons.ContainsKey(conflict.AddonName) ? GraphNodeOrigin.Root : GraphNodeOrigin.Transitive,
                SourceKind = SpecFor(conflict.AddonName, manifest)?.SourceKind,
                Constraint = ConstraintFor(conflict.AddonName, manifest),
                IsOverridden = manifest.Overrides.ContainsKey(conflict.AddonName),
                IsPatched = locked?.PatchHash is not null,
                Status = locked is null ? null : AddonStatusEvaluator.Evaluate(_fileSystem, projectDirectory, locked),
                Dependencies = [],
                Requirements = conflict.Requirements,
            });
        }

        return new GraphReport
        {
            Source = GraphSource.Resolved,
            ProjectDirectory = projectDirectory,
            RootNames = manifest.Addons.Keys.ToList(),
            Addons = nodes,
            Edges = BuildEdges(manifest.Addons.Keys, nodes),
            Dotnet = BuildDotnet(manifest, lockfile, analysis.AddonDeclaredDotnet),
            Conflicts = analysis.Conflicts,
            Warnings = analysis.Warnings,
            Overrides = manifest.Overrides.Keys.ToList(),
        };
    }

    private static IReadOnlyList<GraphRequirement> RequirementsFor(string name, GraphAnalysis analysis) =>
        analysis.RequirementsByName.TryGetValue(name, out var requirements) ? requirements : [];

    private static AddonSpec? SpecFor(string name, ProjectManifest manifest) =>
        manifest.Overrides.TryGetValue(name, out var overrideSpec) ? overrideSpec
        : manifest.Addons.TryGetValue(name, out var spec) ? spec
        : null;

    private static string? ConstraintFor(string name, ProjectManifest manifest)
    {
        var spec = SpecFor(name, manifest);
        return spec is null ? null : AddonRequirement.ConstraintText(spec);
    }

    private static GraphNodeOrigin OriginOf(string name, ProjectManifest manifest, IReadOnlySet<string> reachable) =>
        manifest.Addons.ContainsKey(name) ? GraphNodeOrigin.Root
        : reachable.Contains(name) ? GraphNodeOrigin.Transitive
        : GraphNodeOrigin.Orphan;

    /// <summary>Project→root edges plus every recorded parent→child edge, deduplicated. Edges to
    /// names with no node are dropped: drawing a dangling arrow into nothing is worse than admitting
    /// the child isn't in this picture.</summary>
    private static IReadOnlyList<GraphEdge> BuildEdges(IEnumerable<string> rootNames, IReadOnlyList<GraphNode> nodes)
    {
        var known = nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        var edges = new List<GraphEdge>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string from, string to)
        {
            if (known.Contains(to) && seen.Add(from + " " + to))
            {
                edges.Add(new GraphEdge(from, to));
            }
        }

        foreach (var root in rootNames)
        {
            Add(GraphConflict.ProjectRequirerName, root);
        }

        foreach (var node in nodes)
        {
            foreach (var child in node.Dependencies)
            {
                Add(node.Name, child);
            }
        }

        return edges;
    }

    private static IReadOnlyList<GraphDotnetNode> BuildDotnet(
        ProjectManifest manifest, DotquayLockfile lockfile, IReadOnlyList<DotnetSpec> addonDeclared)
    {
        var resolvedByName = lockfile.Dotnet.ToDictionary(d => d.Name, d => d.Resolved, StringComparer.Ordinal);
        var result = new List<GraphDotnetNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, spec) in manifest.Dotnet)
        {
            seen.Add(name);
            result.Add(new GraphDotnetNode(name, spec.Version, resolvedByName.GetValueOrDefault(name), DeclaredByProject: true));
        }

        // The project's own [dotnet.*] entries always win over an addon-declared one of the same
        // name (requirement P1-5's rule, mirrored here so the panel shows the same winner the
        // installer would pick).
        foreach (var spec in addonDeclared.Where(s => seen.Add(s.Name)))
        {
            result.Add(new GraphDotnetNode(spec.Name, spec.Version, resolvedByName.GetValueOrDefault(spec.Name), DeclaredByProject: false));
        }

        foreach (var locked in lockfile.Dotnet.Where(d => seen.Add(d.Name)))
        {
            result.Add(new GraphDotnetNode(locked.Name, null, locked.Resolved, DeclaredByProject: false));
        }

        return result;
    }

    /// <summary>BFS over the lockfile's own <see cref="LockedAddon.Dependencies"/> edges — the same
    /// reachability rule <see cref="DependencyWorkflow.RemoveAsync"/> prunes with, used here to tell
    /// a legitimately transitive addon apart from a leftover orphan.</summary>
    private static HashSet<string> ReachableNames(DotquayLockfile lockfile, IEnumerable<string> rootNames)
    {
        var lockedByName = lockfile.Addons.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var root in rootNames)
        {
            if (lockedByName.ContainsKey(root) && reachable.Add(root))
            {
                queue.Enqueue(root);
            }
        }

        while (queue.Count > 0)
        {
            foreach (var child in lockedByName[queue.Dequeue()].Dependencies)
            {
                if (lockedByName.ContainsKey(child) && reachable.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return reachable;
    }
}
