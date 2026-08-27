using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;
using Dotquay.Core.Sync;
using Dotquay.Core.Versioning;

namespace Dotquay.Core.Resolve;

/// <summary>
/// Walks the dependency graph breadth-first for dotquay-aware addons (SPEC.md §8, requirement
/// P1-2): starting from the project's top-level <c>[addons.*]</c> entries, reads each resolved
/// addon's own package manifest (SPEC.md §7.2) if it has one, collects every requirer's constraint
/// on every addon name encountered, and unifies each name down to exactly one commit — Godot's flat
/// <c>addons/</c> folder allows exactly one copy of each addon, so there is no "side by side"
/// option. <c>[overrides]</c> in the project manifest wins a name unconditionally, skipping
/// unification (and re-driving discovery of that name's own dependencies from the override, not
/// whatever spec first requested it).
/// </summary>
/// <remarks>
/// This is deliberately not a full constraint solver (SPEC.md §8 says so explicitly): each addon
/// name's own dependencies are discovered exactly once, using whichever spec is chosen for
/// <em>discovery</em> purposes (the override if one exists, otherwise the first requirement seen for
/// that name) — if unification later settles on a different commit than the one discovery used to
/// peek at that addon's manifest, the recorded <see cref="GraphAddonEntry.DependencyNames"/> reflects
/// the discovery-time manifest, not necessarily the finally-chosen one. In practice this only matters
/// when a name's own declared dependencies change meaningfully between the discovery version and the
/// unified version, which is expected to be rare for the kind of properly-tagged, dotquay-aware
/// addons this feature targets (initially: Kani's own).
/// </remarks>
public sealed class DependencyGraphResolver
{
    private const string ProjectRequirerName = "<project>";

    private readonly IFileSystem _fileSystem;
    private readonly IGitClient _gitClient;
    private readonly RepoCache _cache;
    private readonly AddonResolver _addonResolver;
    private readonly string _discoveryStagingRoot;

    /// <param name="discoveryStagingRoot">A scratch directory used only to extract each addon's
    /// subtree far enough to peek at its <c>dotquay.toml</c> — deleted in full once the graph walk
    /// finishes. Distinct from the real install staging path (<c>.dotquay/staging/&lt;name&gt;</c>):
    /// this is a read-only, throwaway concern, not the atomic-swap pipeline.</param>
    public DependencyGraphResolver(IFileSystem fileSystem, IGitClient gitClient, RepoCache cache, string discoveryStagingRoot)
    {
        _fileSystem = fileSystem;
        _gitClient = gitClient;
        _cache = cache;
        _addonResolver = new AddonResolver(gitClient);
        _discoveryStagingRoot = discoveryStagingRoot;
    }

    private sealed record Requirement(string RequirerName, AddonSpec Spec);

    public async Task<GraphResolutionResult> ResolveGraphAsync(
        IReadOnlyDictionary<string, AddonSpec> rootAddons,
        IReadOnlyDictionary<string, AddonSpec> overrides,
        CancellationToken cancellationToken = default)
    {
        var constraintsByName = new Dictionary<string, List<Requirement>>(StringComparer.Ordinal);
        var dependencyNamesByAddon = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var discoveredNames = new HashSet<string>(StringComparer.Ordinal);
        var addonDeclaredDotnet = new Dictionary<string, DotnetSpec>(StringComparer.Ordinal);
        var queue = new Queue<Requirement>();

        foreach (var spec in rootAddons.Values)
        {
            queue.Enqueue(new Requirement(ProjectRequirerName, spec));
        }

        try
        {
            while (queue.Count > 0)
            {
                var requirement = queue.Dequeue();
                var name = requirement.Spec.Name;

                if (!constraintsByName.TryGetValue(name, out var requirementsForName))
                {
                    requirementsForName = [];
                    constraintsByName[name] = requirementsForName;
                }

                requirementsForName.Add(requirement);

                if (!discoveredNames.Add(name))
                {
                    continue; // already expanded this addon's own manifest once
                }

                var specForDiscovery = overrides.TryGetValue(name, out var overrideSpec) ? overrideSpec : requirement.Spec;
                var provisional = await _addonResolver.ResolveAsync(specForDiscovery, cancellationToken);
                var packageManifest = await TryReadPackageManifestAsync(provisional, cancellationToken);

                if (packageManifest is null)
                {
                    continue; // leaf: no dotquay.toml, or one without a valid [package] header
                }

                var childNames = new List<string>();
                foreach (var childSpec in packageManifest.Addons.Values)
                {
                    childNames.Add(childSpec.Name);
                    queue.Enqueue(new Requirement(name, childSpec));
                }

                dependencyNamesByAddon[name] = childNames;

                foreach (var dotnetSpec in packageManifest.Dotnet.Values)
                {
                    // First-discovered wins among addon-declared entries -- there's no good general
                    // way to unify two different NuGet version ranges the way git tags allow (no
                    // "list every version and intersect" primitive dotquay already has for NuGet).
                    // The caller (DependencyWorkflow) additionally lets the *project's own*
                    // [dotnet.*] entries always win over anything discovered here.
                    addonDeclaredDotnet.TryAdd(dotnetSpec.Name, dotnetSpec);
                }
            }
        }
        finally
        {
            _fileSystem.DeleteDirectory(_discoveryStagingRoot);
        }

        var addons = new List<GraphAddonEntry>();
        foreach (var (name, requirements) in constraintsByName)
        {
            var resolved = overrides.TryGetValue(name, out var overrideSpec)
                ? await _addonResolver.ResolveAsync(overrideSpec, cancellationToken)
                : await UnifyAndResolveAsync(name, requirements, cancellationToken);

            var dependencyNames = dependencyNamesByAddon.TryGetValue(name, out var children)
                ? (IReadOnlyList<string>)children
                : [];

            addons.Add(new GraphAddonEntry(resolved, dependencyNames));
        }

        return new GraphResolutionResult(addons, addonDeclaredDotnet.Values.ToList());
    }

    /// <summary>Unifies every requirer's constraint on one addon name down to a single commit, or
    /// throws <see cref="AddonResolutionException"/> naming the requirers that can't be reconciled
    /// (SPEC.md §8: "constraints that cannot be unified... are a hard error").</summary>
    private async Task<ResolvedAddon> UnifyAndResolveAsync(
        string name, IReadOnlyList<Requirement> requirements, CancellationToken cancellationToken)
    {
        if (requirements.Count == 1)
        {
            return await _addonResolver.ResolveAsync(requirements[0].Spec, cancellationToken);
        }

        var distinctGitUrls = requirements.Select(r => r.Spec.Git).Distinct(StringComparer.Ordinal).ToList();
        if (distinctGitUrls.Count > 1)
        {
            throw new AddonResolutionException(
                $"'{name}' is required from different git repositories and can't be unified: "
                + string.Join("; ", requirements.Select(r => $"{Describe(r)} wants {r.Spec.Git}")) + ".");
        }

        var pinRequirements = requirements.Where(r => r.Spec.SourceKind != AddonSourceKind.Version).ToList();
        var versionRequirements = requirements.Where(r => r.Spec.SourceKind == AddonSourceKind.Version).ToList();

        if (pinRequirements.Count > 0)
        {
            var distinctPins = pinRequirements
                .Select(r => r.Spec.SourceKind == AddonSourceKind.Rev ? $"rev:{r.Spec.Rev}" : $"branch:{r.Spec.Branch}")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (distinctPins.Count > 1 || versionRequirements.Count > 0)
            {
                throw DisjointPinsError(name, requirements);
            }

            return await _addonResolver.ResolveAsync(pinRequirements[0].Spec, cancellationToken);
        }

        return await ResolveVersionIntersectionAsync(name, versionRequirements, cancellationToken);
    }

    /// <summary>Every requirement on <paramref name="name"/> is a <c>version</c> constraint against
    /// the same repo — finds the highest tag satisfying all of them simultaneously (SPEC.md §8:
    /// "pick the highest version satisfying all constraints on a name").</summary>
    private async Task<ResolvedAddon> ResolveVersionIntersectionAsync(
        string name, IReadOnlyList<Requirement> versionRequirements, CancellationToken cancellationToken)
    {
        var constraints = new List<VersionConstraint>();
        foreach (var requirement in versionRequirements)
        {
            if (!VersionConstraint.TryParse(requirement.Spec.Version!, out var constraint) || constraint is null)
            {
                throw new AddonResolutionException(
                    $"'{name}': '{requirement.Spec.Version}' (from {Describe(requirement)}) is not a valid version constraint.");
            }

            constraints.Add(constraint);
        }

        var gitUrl = versionRequirements[0].Spec.Git;
        // Simplification: the effective subtree path is taken from whichever requirement happened
        // to be first in the list rather than cross-checked for agreement — Path is a fact about the
        // repo's own layout, not something two consumers of the same addon should realistically
        // disagree about, and SPEC.md §8 already scopes this feature away from being a full solver.
        var effectivePath = versionRequirements[0].Spec.EffectivePath;

        var refs = await _gitClient.ListRemoteRefsAsync(gitUrl, cancellationToken);

        AddonVersion? best = null;
        GitRef? bestRef = null;
        foreach (var gitRef in refs)
        {
            if (gitRef.Kind != GitRefKind.Tag || !AddonVersion.TryParse(gitRef.Name, out var version) || version is null || version.IsPrerelease)
            {
                continue;
            }

            if (!constraints.All(c => c.IsSatisfiedBy(version)))
            {
                continue;
            }

            if (best is null || version.CompareTo(best) > 0)
            {
                best = version;
                bestRef = gitRef;
            }
        }

        if (best is null || bestRef is null)
        {
            throw new AddonResolutionException(
                $"'{name}': no tag satisfies every requested version constraint at once: "
                + string.Join("; ", versionRequirements.Select(r => $"{Describe(r)} wants {r.Spec.Version}"))
                + $". Add an [overrides.{name}] entry in the project manifest to force one.");
        }

        return new ResolvedAddon(name, gitUrl, bestRef.CommitSha, best.OriginalTag, effectivePath);
    }

    private async Task<PackageManifest?> TryReadPackageManifestAsync(ResolvedAddon resolved, CancellationToken cancellationToken)
    {
        var cachedRepoPath = await _cache.GetOrFetchAsync(resolved.Git, resolved.ResolvedSha, cancellationToken);

        var discoveryPath = _fileSystem.CombinePath(_discoveryStagingRoot, resolved.Name);
        try
        {
            SubtreeExtractor.Extract(_fileSystem, cachedRepoPath, resolved.Path, discoveryPath);
        }
        catch (SyncException)
        {
            // The configured subtree doesn't exist / is empty at this commit. That's a real problem
            // if this addon is the one unification finally settles on -- install will report it
            // clearly then. During discovery it just means "nothing to discover here" rather than
            // aborting the whole graph walk over an addon nobody may even end up choosing.
            return null;
        }

        var manifestPath = _fileSystem.CombinePath(discoveryPath, "dotquay.toml");
        if (!_fileSystem.FileExists(manifestPath))
        {
            return null;
        }

        try
        {
            return ManifestReader.ReadPackage(_fileSystem.ReadAllText(manifestPath), manifestPath);
        }
        catch (ManifestParseException)
        {
            // Not every dotquay.toml an addon happens to ship is a valid [package] manifest -- treat
            // as a leaf rather than failing the whole graph walk (SPEC.md §7.2: "third-party addons
            // without this file are leaf nodes"; a malformed one is treated the same way).
            return null;
        }
    }

    private static AddonResolutionException DisjointPinsError(string name, IReadOnlyList<Requirement> requirements) =>
        new(
            $"'{name}' has conflicting pins that can't be unified: "
            + string.Join("; ", requirements.Select(r => $"{Describe(r)} wants {PinDescription(r.Spec)}"))
            + $". Add an [overrides.{name}] entry in the project manifest to force one.");

    private static string PinDescription(AddonSpec spec) => spec.SourceKind switch
    {
        AddonSourceKind.Rev => $"rev {spec.Rev}",
        AddonSourceKind.Branch => $"branch {spec.Branch}",
        _ => $"version {spec.Version}",
    };

    private static string Describe(Requirement requirement) =>
        requirement.RequirerName == ProjectRequirerName ? "the project" : $"'{requirement.RequirerName}'";
}
