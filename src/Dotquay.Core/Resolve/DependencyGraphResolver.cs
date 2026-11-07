using Dotquay.Core.Abstractions;
using Dotquay.Core.Doctor;
using Dotquay.Core.Git;
using Dotquay.Core.Manifest;
using Dotquay.Core.Sync;

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
/// <para>
/// This is deliberately not a full constraint solver (SPEC.md §8 says so explicitly): each addon
/// name's own dependencies are discovered exactly once, using whichever spec is chosen for
/// <em>discovery</em> purposes (the override if one exists, otherwise the first requirement seen
/// for that name) — if unification later settles on a different commit than the one discovery used
/// to peek at that addon's manifest, the recorded <see cref="GraphAddonEntry.DependencyNames"/>
/// reflects the discovery-time manifest, not necessarily the finally-chosen one. In practice this
/// only matters when a name's own declared dependencies change meaningfully between the discovery
/// version and the unified version, which is expected to be rare for the kind of properly-tagged,
/// dotquay-aware addons this feature targets (initially: Kani's own).
/// </para>
/// <para>
/// Two entry points share that one walk (v0.5): <see cref="ResolveGraphAsync"/> for the
/// install/update path, where an unresolvable name is a hard error, and
/// <see cref="AnalyzeGraphAsync"/> for <c>dotquay graph</c>, where it is a
/// <see cref="GraphConflict"/> the editor plugin turns into a version picker (SPEC.md §18). The
/// unification rules live in <see cref="AddonUnifier"/> so both see identical behaviour.
/// </para>
/// </remarks>
public sealed class DependencyGraphResolver {
    private readonly IFileSystem _fileSystem;
    private readonly RepoCache _cache;
    private readonly AddonResolver _addonResolver;
    private readonly AddonUnifier _unifier;
    private readonly string _discoveryStagingRoot;

    /// <param name="discoveryStagingRoot">A scratch directory used only to extract each addon's
    /// subtree far enough to peek at its <c>dotquay.toml</c> — deleted in full once the graph walk
    /// finishes. Distinct from the real install staging path
    /// (<c>.dotquay/staging/&lt;name&gt;</c>): this is a read-only, throwaway concern, not the
    /// atomic-swap pipeline.</param>
    /// <remarks>The git client is wrapped in a <see cref="MemoizingGitClient"/> owned by this
    /// instance: discovery and unification ask the same remotes the same questions, and one
    /// resolver is one resolution, which is exactly the lifetime a ref answer may be trusted for.
    /// Create a new resolver per resolution - never keep one around.</remarks>
    public DependencyGraphResolver(IFileSystem fileSystem, IGitClient gitClient, RepoCache cache,
        string discoveryStagingRoot) {
        _fileSystem = fileSystem;
        _cache = cache;

        var memoizing = MemoizingGitClient.Wrap(gitClient);
        _addonResolver = new AddonResolver(memoizing);
        _unifier = new AddonUnifier(memoizing);
        _discoveryStagingRoot = discoveryStagingRoot;
    }

    /// <summary>The install/update entry point: unifies every name or throws
    /// <see cref="AddonResolutionException"/> naming the requirers that can't be reconciled
    /// (SPEC.md §8: "constraints that cannot be unified... are a hard error").</summary>
    public async Task<GraphResolutionResult> ResolveGraphAsync(
        IReadOnlyDictionary<string, AddonSpec> rootAddons,
        IReadOnlyDictionary<string, AddonSpec> overrides,
        CancellationToken cancellationToken = default) {
        var analysis = await AnalyzeGraphAsync(rootAddons, overrides, cancellationToken);

        if (analysis.Conflicts.Count > 0) {
            throw new AddonResolutionException(analysis.Conflicts[0].Message);
        }

        return new GraphResolutionResult(analysis.Addons, analysis.AddonDeclaredDotnet,
            analysis.Warnings) {
            AddonDeclaredSources = analysis.AddonDeclaredSources,
            DotnetPackagesByAddon = analysis.DotnetPackagesByAddon,
            AddonDotnetRequirements = analysis.AddonDotnetRequirements,
        };
    }

    /// <summary>
    /// The reporting entry point (v0.5): resolves as much of the graph as it can and returns every
    /// name it could not unify as a <see cref="GraphConflict"/> instead of failing on the first
    /// one. Reporting all conflicts at once matters for a UI — resolving them one error message at
    /// a time is exactly the CLI experience the editor plugin exists to replace.
    /// </summary>
    public async Task<GraphAnalysis> AnalyzeGraphAsync(
        IReadOnlyDictionary<string, AddonSpec> rootAddons,
        IReadOnlyDictionary<string, AddonSpec> overrides,
        CancellationToken cancellationToken = default) {
        var walk = await WalkAsync(rootAddons, overrides, cancellationToken);

        var addons = new List<GraphAddonEntry>();
        var conflicts = new List<GraphConflict>();

        foreach (var (name, requirements) in walk.RequirementsByName) {
            var outcome = overrides.TryGetValue(name, out var overrideSpec)
                // An override skips unification entirely and wins unconditionally (SPEC.md §8) —
                // that is precisely what makes it the documented escape hatch out of a conflict.
                ? await _unifier.UnifyAsync(name,
                    [new AddonRequirement(GraphConflict.ProjectRequirerName, overrideSpec)],
                    cancellationToken)
                : await _unifier.UnifyAsync(name, requirements, cancellationToken);

            if (!outcome.IsResolved) {
                conflicts.Add(outcome.Conflict);
                continue;
            }

            var dependencyNames = walk.DependencyNamesByAddon.TryGetValue(name, out var children)
                ? (IReadOnlyList<string>)children
                : [];

            addons.Add(new GraphAddonEntry(outcome.Resolved, dependencyNames));
        }

        return new GraphAnalysis(
            addons,
            walk.AddonDeclaredDotnet,
            conflicts,
            walk.RequirementsByName.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<GraphRequirement>)kv.Value.Select(r => r.ToGraphRequirement())
                .ToList(),
                StringComparer.Ordinal),
            walk.Warnings) {
            AddonDeclaredSources = walk.AddonDeclaredSources,
            DotnetPackagesByAddon = walk.DotnetPackagesByAddon,
            AddonDotnetRequirements = walk.AddonDotnetRequirements,
        };
    }

    private sealed record WalkResult(
        IReadOnlyDictionary<string, List<AddonRequirement>> RequirementsByName,
        IReadOnlyDictionary<string, List<string>> DependencyNamesByAddon,
        IReadOnlyList<DotnetSpec> AddonDeclaredDotnet,
        IReadOnlyList<GraphWarning> Warnings) {
        public IReadOnlyList<AddonDeclaredSource> AddonDeclaredSources { get; init; } = [];

        public IReadOnlyDictionary<string, IReadOnlyList<string>> DotnetPackagesByAddon {
            get; init;
        } =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string,
            IReadOnlyList<AddonDotnetRequirement>> AddonDotnetRequirements { get; init; } =
            new Dictionary<string, IReadOnlyList<AddonDotnetRequirement>>(StringComparer.Ordinal);
    }

    /// <summary>The breadth-first discovery half, shared by both entry points: expands each addon
    /// name's own package manifest exactly once and records who required what. Never unifies —
    /// that's <see cref="AddonUnifier"/>'s job, and keeping the two apart is what lets one walk
    /// serve both a throwing and a reporting caller.</summary>
    private async Task<WalkResult> WalkAsync(
        IReadOnlyDictionary<string, AddonSpec> rootAddons,
        IReadOnlyDictionary<string, AddonSpec> overrides,
        CancellationToken cancellationToken) {
        var requirementsByName = new Dictionary<string,
            List<AddonRequirement>>(StringComparer.Ordinal);
        var dependencyNamesByAddon = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var discoveredNames = new HashSet<string>(StringComparer.Ordinal);
        var addonDeclaredDotnet = new Dictionary<string, DotnetSpec>(StringComparer.Ordinal);
        var addonDotnetRequirements = new Dictionary<string,
            List<AddonDotnetRequirement>>(StringComparer.Ordinal);

        // Only for warning messages — which addon won a package, so a conflict can name both sides
        // instead of saying "something else declared this differently".
        var dotnetDeclaredBy = new Dictionary<string, string>(StringComparer.Ordinal);
        var addonDeclaredSources = new List<AddonDeclaredSource>();
        var dotnetPackagesByAddon = new Dictionary<string,
            IReadOnlyList<string>>(StringComparer.Ordinal);
        var warnings = new List<GraphWarning>();
        var queue = new Queue<AddonRequirement>();

        // Inferred once, from the project's own declarations only. Transitively-discovered URLs are
        // what this gets applied to, so letting them feed back into the inference would let one
        // addon author's house style redirect a host the project never described that way.
        var transport = GitTransportPreference.FromProjectUrls(
            rootAddons.Values.Concat(overrides.Values).Select(spec => spec.Git));

        // Warms the remote lookups the walk is about to make, a few at a time, without changing
        // what it makes or in which order (see RefPrefetcher). Disposed in the finally below, so no
        // prefetch outlives this walk.
        var prefetcher = new RefPrefetcher(_addonResolver, cancellationToken);

        foreach (var spec in rootAddons.Values) {
            queue.Enqueue(new AddonRequirement(GraphConflict.ProjectRequirerName, spec));
            prefetcher.Warm(SpecForDiscovery(spec, overrides));
        }

        try {
            while (queue.Count > 0) {
                var requirement = queue.Dequeue();
                var name = requirement.Spec.Name;

                if (!requirementsByName.TryGetValue(name, out var requirementsForName)) {
                    requirementsForName = [];
                    requirementsByName[name] = requirementsForName;
                }

                requirementsForName.Add(requirement);

                if (!discoveredNames.Add(name)) {
                    continue; // already expanded this addon's own manifest once
                }

                var specForDiscovery = SpecForDiscovery(requirement.Spec, overrides);
                var packageManifest = await TryReadPackageManifestAsync(
                    specForDiscovery, requirement.Requirer, warnings, cancellationToken);

                if (packageManifest is null) {
                    // leaf: no dotquay.toml, one without a valid [package] header, or unresolvable
                    continue;
                }

                var childNames = new List<string>();
                foreach (var declaredChildSpec in packageManifest.Addons.Values) {
                    var childSpec = ApplyTransportPreference(transport, name, declaredChildSpec,
                        warnings);
                    childNames.Add(childSpec.Name);
                    queue.Enqueue(new AddonRequirement(name, childSpec));
                    prefetcher.Warm(SpecForDiscovery(childSpec, overrides));
                }

                dependencyNamesByAddon[name] = childNames;

                foreach (var dotnetSpec in packageManifest.Dotnet.Values) {
                    // Every addon's range is kept: the version must satisfy all of them, as for git
                    // addons (DESIGN-v0.21 §3.5). The caller lets the *project's own* [dotnet.*]
                    // entries win over anything discovered here.
                    if (!addonDotnetRequirements.TryGetValue(dotnetSpec.Name, out var ranges)) {
                        addonDotnetRequirements[dotnetSpec.Name] = ranges = [];
                    }

                    ranges.Add(new AddonDotnetRequirement(name, dotnetSpec.Version));

                    // The entry itself (asset metadata, feed pin) is still first-discovered-wins.
                    if (addonDeclaredDotnet.TryAdd(dotnetSpec.Name, dotnetSpec)) {
                        dotnetDeclaredBy[dotnetSpec.Name] = name;
                        continue;
                    }

                    // Losing on version costs nothing, since every range counts. Losing on asset
                    // metadata is worth reporting: it is not a range to be narrowed but a statement
                    // about how the package must be wired up, and the loser silently gets the
                    // winner's wiring.
                    var winner = addonDeclaredDotnet[dotnetSpec.Name];
                    if (!winner.Assets.SameAs(dotnetSpec.Assets)) {
                        warnings.Add(new GraphWarning(
                            name,
                            GraphWarningKind.DotnetAssetMetadataConflict,
                            $"'{name}' and "
                            + $"'{dotnetDeclaredBy.GetValueOrDefault(
                                dotnetSpec.Name, "another addon")}' "
                            + $"declare different asset metadata for the NuGet package "
                            + $"'{dotnetSpec.Name}'. Using "
                            + $"'{dotnetDeclaredBy.GetValueOrDefault(
                                dotnetSpec.Name, "the first")}'’s. Declare "
                            + $"[dotnet.\"{dotnetSpec.Name}\"] in your own dotquay.toml to settle "
                            + $"it."));
                    }
                }

                if (packageManifest.Dotnet.Count > 0) {
                    dotnetPackagesByAddon[name] = packageManifest.Dotnet.Values.Select(d => d.Name)
                        .ToList();
                }

                // Collected, never applied here. Whether any of these feeds is actually used is the
                // consuming project's decision (SourceAdopter, SPEC.md §11.3) — the walk's job is
                // only to surface what the addons asked for.
                foreach (var declaredSource in packageManifest.NuGetSources) {
                    addonDeclaredSources.Add(new AddonDeclaredSource(name, declaredSource));
                }
            }
        } finally {
            await prefetcher.DisposeAsync();
            _fileSystem.DeleteDirectory(_discoveryStagingRoot);
        }

        return new WalkResult(requirementsByName, dependencyNamesByAddon,
            addonDeclaredDotnet.Values.ToList(), warnings) {
            AddonDeclaredSources = addonDeclaredSources,
            DotnetPackagesByAddon = dotnetPackagesByAddon,
            AddonDotnetRequirements = addonDotnetRequirements.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<AddonDotnetRequirement>)kv.Value,
                StringComparer.Ordinal),
        };
    }

    /// <summary>The spec a name is discovered (and its package manifest read) with: its override
    /// when the project declares one, otherwise whichever requirement reached it first.</summary>
    private static AddonSpec SpecForDiscovery(AddonSpec requested,
        IReadOnlyDictionary<string, AddonSpec> overrides) =>
        overrides.TryGetValue(requested.Name, out var overrideSpec) ? overrideSpec : requested;

    /// <summary>
    /// Re-expresses a discovered dependency's URL over the transport this project uses for that
    /// host, recording a warning when that changes anything.
    /// </summary>
    /// <remarks>
    /// <para>Only the URL moves. Name, pin, subtree path and install folder are the addon author's
    /// statements about their own addon and are copied through untouched -- this is a routing
    /// decision, not a redefinition of the dependency, and <see cref="GitUrl"/> guarantees the
    /// rewritten URL names the same repository.</para>
    /// <para>Warned about rather than done quietly: the lockfile ends up naming a URL that appears
    /// in no manifest anywhere, and somebody reading it later deserves to find out here rather than
    /// by diffing it against the addon's own dotquay.toml.</para>
    /// </remarks>
    private static AddonSpec ApplyTransportPreference(
        GitTransportPreference transport, string requirerName, AddonSpec declared,
        List<GraphWarning> warnings) {
        if (!transport.TryRewrite(declared.Git, out var rewritten)) {
            return declared;
        }

        warnings.Add(new GraphWarning(
            declared.Name,
            GraphWarningKind.GitTransportRewritten,
            $"'{requirerName}' declares '{declared.Name}' as {declared.Git}, but this project "
            + $"reaches that host over a different transport. Fetching {rewritten} instead -- it "
            + $"is the same repository. Add an [overrides.{declared.Name}] entry to pin the URL "
            + $"yourself."));

        return declared with { Git = rewritten };
    }
    /// <param name="requirer">Who asked for this addon —
    /// <see cref="GraphConflict.ProjectRequirerName"/> for the project's own <c>[addons.*]</c>,
    /// otherwise the name of the addon whose package manifest declares it. Only used to make a
    /// layout warning name the manifest that needs editing: a consumer who sees "vrm points at a
    /// project" has no way to know that the <c>path</c> in question lives in vbeami's manifest, not
    /// their own.</param>
    private async Task<PackageManifest?> TryReadPackageManifestAsync(
        AddonSpec specForDiscovery, string requirer, List<GraphWarning> warnings,
        CancellationToken cancellationToken) {
        ResolvedAddon resolved;
        try {
            resolved = await _addonResolver.ResolveAsync(specForDiscovery, cancellationToken);
        } catch (AddonResolutionException) {
            // The discovery spec doesn't resolve (e.g. a constraint no tag satisfies). Unification
            // will report that properly per-name; during discovery it just means there is nothing
            // to peek at, rather than a reason to abort the whole walk — which is what lets
            // AnalyzeGraphAsync surface *every* conflict instead of only the first.
            return null;
        }

        var cachedRepoPath = await _cache.GetOrFetchAsync(resolved.Git, resolved.ResolvedSha,
            cancellationToken);

        var discoveryPath = _fileSystem.CombinePath(_discoveryStagingRoot, resolved.Name);
        try {
            SubtreeExtractor.Extract(_fileSystem, cachedRepoPath, resolved.Path, discoveryPath);
        } catch (SyncException) {
            // The configured subtree doesn't exist / is empty at this commit. That's a real problem
            // if this addon is the one unification finally settles on -- install will report it
            // clearly then. During discovery it just means "nothing to discover here" rather than
            // aborting the whole graph walk over an addon nobody may even end up choosing.
            return null;
        }

        var subtreeIsProject = WarnIfSubtreeIsAGodotProject(resolved, specForDiscovery, requirer,
            discoveryPath, warnings);

        var manifestPath = _fileSystem.CombinePath(discoveryPath, "dotquay.toml");
        if (!_fileSystem.FileExists(manifestPath)) {
            WarnIfManifestIsOutsideTheSubtree(resolved, cachedRepoPath, warnings);
            return null;
        }

        try {
            return ManifestReader.ReadPackage(_fileSystem.ReadAllText(manifestPath), manifestPath);
        } catch (ManifestParseException) when (subtreeIsProject) {
            // A project root carries the project's own [project] manifest, which is not a package
            // manifest and was never meant to be one. The warning above already explains the whole
            // situation; a second one about the header would send the reader off to add [package]
            // to a file that should not be read at all.
            return null;
        } catch (ManifestParseException ex) {
            // Not every dotquay.toml an addon happens to ship is a valid [package] manifest --
            // treat as a leaf rather than failing the whole graph walk (SPEC.md §7.2: "third-party
            // addons without this file are leaf nodes"; a malformed one is treated the same way).
            // Since v0.5 that decision is still made, but no longer silently: a file sitting right
            // next to plugin.cfg and named dotquay.toml is a clear statement of intent, so being
            // ignored without a word is the single most confusing outcome available.
            warnings.Add(new GraphWarning(
                resolved.Name,
                GraphWarningKind.PackageManifestNotReadable,
                $"'{resolved.Name}' ships a dotquay.toml that isn't a valid package manifest, so "
                + $"its own dependencies are being ignored. {ex.Message}"));

            return null;
        }
    }

    /// <summary>
    /// The subtree exists and extracts, but is a Godot project root rather than an addon
    /// (<see cref="GodotProjectRootChecker"/>). Returns whether it is, so the caller can stop
    /// reading the project's own manifest as if it were a package manifest.
    /// </summary>
    /// <remarks>
    /// Warned about, never corrected. The folder-case correction (SPEC.md §7.1.1) may fix
    /// capitalisation because that changes nothing about <em>what</em> is installed; silently
    /// swapping <c>path = "."</c> for <c>addons/vrm</c> would change identity and content, and
    /// would have to guess between several plugins when a project ships more than one. The message
    /// instead names the manifest that has to change and the exact override that pins it meanwhile.
    /// </remarks>
    private bool WarnIfSubtreeIsAGodotProject(
        ResolvedAddon resolved, AddonSpec spec, string requirer, string discoveryPath,
        List<GraphWarning> warnings) {
        var finding = GodotProjectRootChecker.Check(_fileSystem, discoveryPath, resolved.Name,
            resolved.Path);
        if (finding is null) {
            return false;
        }

        var declaredIn = requirer == GraphConflict.ProjectRequirerName
            ? $"this project's dotquay.toml ([addons.{resolved.Name}])"
            : $"'{requirer}''s package manifest";

        var (sourceFlag, sourceValue) = spec switch {
            { Version: not null } => ("--version", spec.Version),
            { Branch: not null } => ("--branch", spec.Branch),
            _ => ("--rev", spec.Rev ?? resolved.ResolvedSha),
        };

        var fix = finding.SuggestedPath is null
            ? $"Set path in {declaredIn} to the folder that holds this addon's plugin.cfg."
            : $"Set path = \"{finding.SuggestedPath}\" in {declaredIn}, or pin it from here: "
              + GodotProjectRootChecker.OverrideCommand(resolved.Name, sourceFlag, sourceValue,
                  finding.SuggestedPath) + ".";

        warnings.Add(new GraphWarning(
            resolved.Name,
            GraphWarningKind.SubtreeIsGodotProject,
            $"'{resolved.Name}' points at a Godot project, not an addon: {declaredIn} declares it "
            + $"with path = \"{resolved.Path}\", but that subtree has a project.godot and no "
            + $"plugin.cfg — {GodotProjectRootChecker.DescribeWhereTheAddonIs(finding)}. Installed "
            + $"as-is, the whole project lands under addons/{resolved.EffectiveFolder}/ and Godot "
            + $"finds no plugin there. {fix}"));

        return true;
    }

    /// <summary>
    /// The subtree has no <c>dotquay.toml</c>. That is completely normal for a third-party addon —
    /// but if the repository has one <em>somewhere else</em>, the author almost certainly meant it
    /// to be the package manifest and put it in the wrong place (typically the repo root, or the
    /// Godot project folder that contains <c>addons/</c>). Only the subtree is ever extracted and
    /// installed, so a manifest outside it can never reach a consumer.
    /// </summary>
    /// <remarks>
    /// Searches the cached repo, which is already on disk — no extra fetch. The subtree itself is
    /// excluded from the search because the caller already established there is nothing there.
    /// </remarks>
    private void WarnIfManifestIsOutsideTheSubtree(
        ResolvedAddon resolved, string cachedRepoPath, List<GraphWarning> warnings) {
        var subtreePrefix = NormalizeRelative(resolved.Path);

        foreach (var file in _fileSystem.EnumerateFiles(cachedRepoPath)) {
            if (!file.EndsWith("dotquay.toml", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var relative = NormalizeRelative(file[cachedRepoPath.Length..]);
            if (subtreePrefix.Length > 0
                && relative.StartsWith(subtreePrefix + "/", StringComparison.OrdinalIgnoreCase)) {
                continue; // inside the subtree after all — nothing to warn about
            }

            warnings.Add(new GraphWarning(
                resolved.Name,
                GraphWarningKind.PackageManifestOutsideSubtree,
                $"'{resolved.Name}' has no dotquay.toml inside '{resolved.Path}', but its "
                + $"repository has one at '{relative}'. Only the subtree is installed, so that "
                + $"file can't declare dependencies for consumers — move a [package] manifest into "
                + $"'{resolved.Path}' (SPEC.md §7.2). Until then '{resolved.Name}' is treated as "
                + $"having no dependencies."));

            return; // one warning per addon is enough to send someone to the right place
        }
    }

    /// <summary>Normalises a path fragment to forward slashes with no leading or trailing
    /// separator, so a manifest's <c>path = "godot/addons/foo/"</c> compares equal to the same
    /// subtree written any other way.</summary>
    private static string NormalizeRelative(string path) =>
        path.Replace('\\', '/').Trim('/');
}
