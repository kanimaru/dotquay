using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Git;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Progress;
using Dotquay.Core.Resolve;

namespace Dotquay.Core.Workflows;

/// <summary>
/// The read-only half of <c>add</c>: everything up to the point where anything would be written.
/// </summary>
/// <remarks>
/// Split into its own file because the distinction it draws is worth being able to check at a glance —
/// nothing here calls <c>WriteManifest</c>, <c>WriteLockfile</c>, or the installer, and a reviewer can
/// confirm that by reading one file rather than tracing branches through a 1,800-line orchestrator.
/// </remarks>
public sealed partial class DependencyWorkflow
{
    /// <summary>
    /// Resolves what <c>dotquay add &lt;git-url&gt;</c> would do without doing any of it: the full
    /// transitive addon graph, the NuGet packages those addons declare, and the feeds they would ask
    /// the project to take on (SPEC.md §6, §11.3).
    /// </summary>
    /// <remarks>
    /// This is a genuine graph walk against live git refs, not a guess — which is what makes it slow
    /// enough to want a progress reporter, and truthful enough to confirm an install against. The only
    /// thing it touches on disk is the throwaway discovery staging directory
    /// <see cref="DependencyGraphResolver"/> cleans up after itself.
    /// <para>
    /// Feeds are split by whether they need a human: <see cref="AddPreview.SourcesToAdopt"/> would be
    /// taken on silently (trusted host, or listed in <paramref name="preApprovedSourceUrls"/>), and
    /// <see cref="AddPreview.SourcesNeedingApproval"/> would not. Passing the latter's URLs back as
    /// <c>--adopt-source</c> on the real add is what completes the two-phase flow.
    /// </para>
    /// </remarks>
    /// <param name="preApprovedSourceUrls">Feed URLs the caller has already agreed to, so a second
    /// preview after a user says yes reports the same decision the real add will make.</param>
    public async Task<AddPreview> PreviewAddAsync(
        string projectDirectory,
        string gitUrl,
        AddonAddOptions? options = null,
        IReadOnlyList<string>? preApprovedSourceUrls = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AddonAddOptions();
        options.Validate();

        var (manifest, manifestPath) = ReadManifestOrThrow(projectDirectory);
        var (name, spec) = await PrepareAddSpecAsync(manifest, manifestPath, gitUrl, options, cancellationToken);

        var candidateManifest = manifest with
        {
            Addons = new Dictionary<string, AddonSpec>(manifest.Addons) { [name] = spec },
        };

        var (lockfile, _) = ReadOrCreateLockfile(projectDirectory);
        var previousAddonsByName = lockfile.Addons.ToDictionary(a => a.Name, StringComparer.Ordinal);

        var graph = await ResolveGraphForPreviewAsync(
            projectDirectory,
            candidateManifest,
            BuildRootSpecs(candidateManifest, previousAddonsByName, new HashSet<string>(StringComparer.Ordinal) { name }),
            cancellationToken);

        // Evaluated against the *unmodified* manifest, because adoption is a question about what the
        // project trusts today — the candidate manifest's new [addons.*] entry has no bearing on it.
        var adoption = new SourceAdopter(PreApprovedSourceApproval.For(preApprovedSourceUrls)).Evaluate(
            manifest,
            graph.AddonDeclaredSources.Select(s => (s.AddonName, s.Source)).ToList(),
            graph.DotnetPackagesByAddon);

        var declaringAddonByUrl = graph.AddonDeclaredSources
            .GroupBy(s => s.Source.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().AddonName, StringComparer.OrdinalIgnoreCase);

        return new AddPreview
        {
            Name = name,
            Git = gitUrl,
            Addons = BuildPreviewAddons(graph, previousAddonsByName, name),
            Dotnet = BuildPreviewDotnet(manifest, lockfile, graph),
            SourcesToAdopt = adoption.Adopted
                .Select(source => ToPreviewSource(
                    source,
                    source.DeclaredByAddon ?? Lookup(declaringAddonByUrl, source.Url),
                    manifest.TrustedHosts,
                    graph.DotnetPackagesByAddon))
                .ToList(),
            SourcesNeedingApproval = adoption.Refused
                .Select(request => ToPreviewSource(
                    request.Source, request.AddonName, manifest.TrustedHosts, graph.DotnetPackagesByAddon))
                .ToList(),
            Warnings = graph.Warnings.Select(w => w.Message).ToList(),
        };
    }

    /// <summary>
    /// Derives the addon's name from its URL and builds the manifest spec <c>add</c> would write,
    /// refusing a name the manifest already declares.
    /// </summary>
    /// <remarks>
    /// Shared verbatim by <see cref="AddAsync"/> and <see cref="PreviewAddAsync"/>. Two copies of
    /// "which tag would we pick" is precisely the drift that would let a preview promise one version
    /// and the add install another.
    /// </remarks>
    private async Task<(string Name, AddonSpec Spec)> PrepareAddSpecAsync(
        ProjectManifest manifest,
        string manifestPath,
        string gitUrl,
        AddonAddOptions options,
        CancellationToken cancellationToken)
    {
        var name = DeriveAddonName(gitUrl);
        if (manifest.Addons.ContainsKey(name))
        {
            throw new WorkflowException(
                $"'{name}' is already declared in {manifestPath}. Use 'dotquay update {name}' instead.");
        }

        // An explicitly named source skips the newest-tag guess entirely — the whole point of saying
        // "--branch main" is that you do not want whatever the tag heuristic would have chosen.
        var spec = options.HasExplicitSource
            ? new AddonSpec
            {
                Name = name,
                Git = gitUrl,
                Version = options.Version,
                Branch = options.Branch,
                Rev = options.Rev,
                Path = options.Path,
                InstallAs = options.InstallAs,
            }
            : await BuildDefaultSpecAsync(name, gitUrl, options.Path, options.InstallAs, cancellationToken);

        return (name, spec);
    }

    /// <summary>The graph walk on its own, with the same exception translation
    /// <see cref="SyncAddonGraphAsync"/> applies — a conflict must surface as the same
    /// <see cref="WorkflowException"/> in a preview as in a real add, or the plugin would classify the
    /// same failure two different ways.</summary>
    private async Task<GraphResolutionResult> ResolveGraphForPreviewAsync(
        string projectDirectory,
        ProjectManifest manifest,
        IReadOnlyDictionary<string, AddonSpec> rootSpecs,
        CancellationToken cancellationToken)
    {
        _progress.Report("resolving the dependency graph");

        var discoveryRoot = _fileSystem.CombinePath(
            projectDirectory, ProjectLayout.DotquayDirectory, "staging", "preview-discovery");
        var resolver = new DependencyGraphResolver(_fileSystem, _gitClient, _repoCache, discoveryRoot);

        try
        {
            return await resolver.ResolveGraphAsync(rootSpecs, manifest.Overrides, cancellationToken);
        }
        catch (AddonResolutionException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }
        catch (GitCommandException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }
    }

    /// <summary>Every addon in the resolved graph, annotated with whether it would actually move.</summary>
    /// <remarks>
    /// Rows that would not change are kept rather than filtered out: a user confirming an install
    /// wants to see the shape of the graph they are agreeing to, and a list that silently omits the
    /// diamond's shared corner misrepresents it. <see cref="PreviewAddon.IsNew"/> carries the
    /// distinction instead, so the UI decides how to present it.
    /// </remarks>
    private static List<PreviewAddon> BuildPreviewAddons(
        GraphResolutionResult graph,
        IReadOnlyDictionary<string, LockedAddon> previousAddonsByName,
        string requestedName)
    {
        var requirersByName = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var entry in graph.Addons)
        {
            foreach (var dependency in entry.DependencyNames)
            {
                if (!requirersByName.TryGetValue(dependency, out var requirers))
                {
                    requirersByName[dependency] = requirers = new SortedSet<string>(StringComparer.Ordinal);
                }

                requirers.Add(entry.Resolved.Name);
            }
        }

        return graph.Addons
            .Select(entry =>
            {
                var resolved = entry.Resolved;
                previousAddonsByName.TryGetValue(resolved.Name, out var previous);

                var requiredBy = requirersByName.TryGetValue(resolved.Name, out var requirers)
                    ? requirers.ToList()
                    : [];

                // The addon the user actually typed is always attributed to the project, even when
                // something else in the graph happens to require it too — "you asked for this" is the
                // one label the confirmation dialog cannot get wrong.
                if (resolved.Name == requestedName || requiredBy.Count == 0)
                {
                    requiredBy.Insert(0, GraphConflictRequirer);
                }

                return new PreviewAddon(
                    resolved.Name,
                    resolved.Git,
                    resolved.DisplayVersion,
                    resolved.ResolvedSha,
                    resolved.EffectiveFolder,
                    IsNew: previous is null || previous.Resolved != resolved.ResolvedSha,
                    PreviousResolved: previous?.Resolved,
                    requiredBy);
            })
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The synthetic requirer name the resolver and the wire format both use for the
    /// project's own entries.</summary>
    private const string GraphConflictRequirer = GraphConflict.ProjectRequirerName;

    /// <summary>
    /// The NuGet side of the preview: what the project already declares, plus what the graph's addons
    /// would union in (requirement P1-5).
    /// </summary>
    private static List<PreviewDotnet> BuildPreviewDotnet(
        ProjectManifest manifest, DotquayLockfile lockfile, GraphResolutionResult graph)
    {
        var lockedNames = lockfile.Dotnet.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var declaringAddonByPackage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (addonName, packageIds) in graph.DotnetPackagesByAddon)
        {
            foreach (var packageId in packageIds)
            {
                declaringAddonByPackage.TryAdd(packageId, addonName);
            }
        }

        var rows = manifest.Dotnet.Values
            .Select(spec => new PreviewDotnet(
                spec.Name,
                spec.Version,
                DeclaredByAddon: null,
                IsNew: !lockedNames.Contains(spec.Name)))
            .ToList();

        var seen = rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in graph.AddonDeclaredDotnet)
        {
            if (!seen.Add(spec.Name))
            {
                continue;
            }

            rows.Add(new PreviewDotnet(
                spec.Name,
                spec.Version,
                DeclaredByAddon: Lookup(declaringAddonByPackage, spec.Name),
                IsNew: !lockedNames.Contains(spec.Name)));
        }

        return rows.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
    }

    private static PreviewSource ToPreviewSource(
        NuGetSourceSpec source,
        string declaredByAddon,
        IReadOnlyList<string> trustedHosts,
        IReadOnlyDictionary<string, IReadOnlyList<string>> packagesByAddon) =>
        new(
            source.Name,
            source.Url,
            declaredByAddon,
            SourceAdopter.IsTrusted(trustedHosts, source.Url),
            source.Packages,
            packagesByAddon.TryGetValue(declaredByAddon, out var ids) ? ids : []);

    /// <summary>A missing attribution is shown as "unknown" rather than crashing the preview: the
    /// whole payload is informational, and a feed whose declarer can't be named is still a feed the
    /// user needs to see.</summary>
    private static string Lookup(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) ? value : "unknown";
}
