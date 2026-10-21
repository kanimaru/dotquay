using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Git;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Progress;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;

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
        var (name, spec, subtree) = await PrepareAddSpecAsync(
            manifest, manifestPath, gitUrl, options, previewing: true, cancellationToken);

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

            // Null whenever the guess was right, a --path was given, or the repository gave no clear
            // answer (which throws rather than reporting). Non-null means this preview resolved
            // against a subtree dotquay picked, and the caller owes the user a question before it
            // runs the real add.
            Subtree = ToSuggestion(subtree),
        };
    }

    /// <summary>Turns an applied finding into the thing a UI renders and replays.</summary>
    /// <remarks>
    /// The explanation is built here, once, rather than in each UI: the CLI's printout and the editor
    /// plugin's dialog describing the same finding two different ways is the kind of drift that makes
    /// a user distrust both. <see cref="SubtreeSuggestion.Path"/> and
    /// <see cref="SubtreeSuggestion.Folder"/> are also literally the <c>--path</c> and
    /// <c>--install-as</c> values to pass back, so a caller confirms by echoing what it showed.
    /// </remarks>
    private static SubtreeSuggestion? ToSuggestion(SubtreeFinding? finding)
    {
        if (finding?.Suggestion is not { } suggestion)
        {
            return null;
        }

        var wanted = SubtreeCandidates.LastSegment(finding.GuessedPath);

        return new SubtreeSuggestion(
            finding.GuessedPath,
            suggestion.Path,
            suggestion.Folder,
            suggestion.Reason,
            $"dotquay looked for {finding.GuessedPath} and found '{suggestion.Path}' — "
            + $"{suggestion.Explain(wanted)}.");
    }

    /// <summary>
    /// Chooses the addon's name (<see cref="AddonNaming"/>) and builds the manifest spec <c>add</c>
    /// would write, refusing a re-add of an addon the manifest already declares.
    /// </summary>
    /// <remarks>
    /// Shared verbatim by <see cref="AddAsync"/> and <see cref="PreviewAddAsync"/>. Two copies of
    /// "which tag would we pick" is precisely the drift that would let a preview promise one version
    /// and the add install another.
    /// </remarks>
    /// <param name="previewing">True for <see cref="PreviewAddAsync"/>. A preview never asks and
    /// never refuses: it applies a subtree suggestion so the graph it reports is the graph the
    /// confirmed add would install, and hands the suggestion back for the caller's own UI to put the
    /// question to the user. The real <c>add</c> asks <see cref="SubtreeChoice"/> instead.</param>
    private async Task<AddSpecPlan> PrepareAddSpecAsync(
        ProjectManifest manifest,
        string manifestPath,
        string gitUrl,
        AddonAddOptions options,
        bool previewing,
        CancellationToken cancellationToken)
    {
        // One authority for the manifest key. The URL's name as always -- but a second addon out of a
        // repository that ships several falls back to its --install-as / --path leaf instead of being
        // refused as though it were the first one again.
        var name = AddonNaming.Choose(gitUrl, options, manifest.Addons, manifestPath).Name;

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

        return await PlanSubtreeAsync(spec, previewing, cancellationToken);
    }

    /// <summary>
    /// Settles where in its own repository the addon actually is, before anything is resolved for
    /// real (docs/DESIGN-v0.20-SUBTREE-GUESS.md §5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subtree <c>add</c> guesses is <c>addons/&lt;name&gt;</c>, derived from the URL, and that
    /// guess is wrong whenever a repository's own name and its addon folder disagree by more than
    /// capitalisation — <c>SproutyLabs/SproutyDialogs</c> over <c>addons/sprouty_dialogs</c>, which is
    /// the majority convention split in the Godot ecosystem rather than an exotic case. Without this,
    /// the guess survives all the way to the installer and fails there, after the fetch, with a
    /// message naming only the path that didn't work.
    /// </para>
    /// <para>
    /// An explicitly stated <c>--path</c> short-circuits the whole thing: probing it would mean
    /// second-guessing something the user just said, and the question this asks has already been
    /// answered. That is also the channel a UI replays a confirmed suggestion through, so the
    /// plugin's "yes" arrives here as a path and never re-asks.
    /// </para>
    /// <para>
    /// <see cref="SubtreeCandidate.Folder"/> is applied as <c>install_as</c> alongside the path. A
    /// Godot addon refers to its own files by absolute <c>res://addons/&lt;folder&gt;/</c> paths,
    /// which are case- and word-sensitive strings even where the filesystem is not — so installing
    /// <c>sprouty_dialogs</c>' content into <c>addons/SproutyDialogs</c> reproduces the Gut bug in
    /// full (SPEC.md §7.1.1): permanently <c>modified</c> on Windows, broken outright on Linux. The
    /// manifest key is left alone; this corrects layout, never identity.
    /// </para>
    /// </remarks>
    private async Task<AddSpecPlan> PlanSubtreeAsync(
        AddonSpec spec, bool previewing, CancellationToken cancellationToken)
    {
        if (spec.Path is not null)
        {
            return new AddSpecPlan(spec.Name, spec, Subtree: null);
        }

        var finding = await ProbeSubtreeAsync(spec, cancellationToken);

        if (finding is null)
        {
            return new AddSpecPlan(spec.Name, spec, Subtree: null);
        }

        if (finding.Suggestion is null)
        {
            throw new WorkflowException(DescribeUnguessableSubtree(spec, finding));
        }

        // A preview reports the question rather than asking it, exactly as it does for feed
        // adoption — and applies the answer it is proposing, so the graph it shows is the graph the
        // confirmed add installs rather than one resolved from a path that isn't there.
        if (!previewing && !SubtreeChoice.Accept(
            new SubtreeChoiceRequest(spec.Name, spec.Git, finding, finding.Suggestion)))
        {
            throw new WorkflowException(DescribeUnguessableSubtree(spec, finding));
        }

        return new AddSpecPlan(spec.Name, Apply(spec, finding.Suggestion), finding);
    }

    /// <summary>Writes an accepted candidate into the spec that is about to become an
    /// <c>[addons.&lt;name&gt;]</c> entry — so the question is answered once and then read from the
    /// manifest by everyone else, on every machine, forever.</summary>
    private static AddonSpec Apply(AddonSpec spec, SubtreeCandidate suggestion) => spec with
    {
        Path = suggestion.Path,

        // Null rather than a redundant restatement when the folder already equals the addon's name:
        // `install_as` exists to record a disagreement, and writing one that agrees is noise in a
        // file people read.
        InstallAs = string.Equals(suggestion.Folder, spec.Name, StringComparison.Ordinal)
            ? null
            : suggestion.Folder,
    };

    /// <summary>
    /// Fetches the addon's commit and asks the repository where the addon really is.
    /// </summary>
    /// <remarks>
    /// One extra ref query ahead of the graph walk; the fetch itself is shared through
    /// <see cref="RepoCache"/>, and the walk is about to want this very commit. Every failure is
    /// swallowed: resolution and transport problems are the resolver's to report a moment later, and
    /// a probe is not allowed to be the thing that reports them — badly, and in terms of a subtree
    /// nobody has asked about yet.
    /// </remarks>
    private async Task<SubtreeFinding?> ProbeSubtreeAsync(AddonSpec spec, CancellationToken cancellationToken)
    {
        try
        {
            _progress.Report($"checking where '{spec.Name}' lives in its repository");

            var resolved = await _resolver.ResolveAsync(spec, cancellationToken);
            var cachedRepoPath = await _repoCache.GetOrFetchAsync(spec.Git, resolved.ResolvedSha, cancellationToken);

            return SubtreeCandidates.Find(_fileSystem, cachedRepoPath, spec.EffectivePath);
        }
        catch (AddonResolutionException)
        {
            return null;
        }
        catch (GitCommandException)
        {
            return null;
        }
        catch (SyncException)
        {
            return null;
        }
    }

    /// <summary>What was looked for, what the repository holds instead, and the exact flags that
    /// settle it.</summary>
    /// <remarks>
    /// Shared by the no-suggestion case and the declined-suggestion case, because the reader is in
    /// the same position in both: they still have to name the subtree themselves, and the only
    /// difference is whether dotquay can propose the value. A declined suggestion still prints its
    /// flags — "no, not like that" and "no, never" are the same keypress, and whoever meant the first
    /// should not have to run the add again to find out what to type.
    /// </remarks>
    private static string DescribeUnguessableSubtree(AddonSpec spec, SubtreeFinding finding)
    {
        var conjunction = finding.Suggestion is null ? "and" : "but";
        var message =
            $"'{spec.Name}' isn't at {finding.GuessedPath} in {spec.Git}, {conjunction} "
            + $"{finding.DescribeWhereTheAddonIs()}. dotquay never guesses an addon's layout on its own "
            + "(SPEC.md §7.1), so ";

        if (finding.Suggestion is null)
        {
            return message
                + "re-run with --path <subtree> (and --install-as <folder> when the folder under addons/ has to "
                + "differ from the addon's name) to say which one it is.";
        }

        var installAs = string.Equals(finding.Suggestion.Folder, spec.Name, StringComparison.Ordinal)
            ? string.Empty
            : $" --install-as {finding.Suggestion.Folder}";

        return message + $"re-run with: --path {finding.Suggestion.Path}{installAs}";
    }

    /// <summary>
    /// The spec <c>add</c> would write, plus the subtree suggestion that shaped it.
    /// </summary>
    /// <param name="Subtree">Non-null only when dotquay's <c>addons/&lt;name&gt;</c> guess was wrong
    /// and its <see cref="SubtreeFinding.Suggestion"/> was applied — by confirmation on a real add,
    /// or provisionally on a preview, which is what a UI needs in order to ask. The whole finding
    /// rather than just the chosen candidate, so a caller can say what was looked for as well as what
    /// was found.</param>
    private sealed record AddSpecPlan(string Name, AddonSpec Spec, SubtreeFinding? Subtree);

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
