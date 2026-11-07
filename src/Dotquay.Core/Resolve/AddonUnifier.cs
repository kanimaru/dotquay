using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;
using Dotquay.Core.Versioning;

namespace Dotquay.Core.Resolve;

/// <summary>
/// Unifies every requirement collected on a single addon name down to one commit, or reports why it
/// can't (SPEC.md §8). Extracted out of <see cref="DependencyGraphResolver"/> in v0.5 so the same
/// rules serve two callers with opposite needs: the install/update path, which wants a hard failure,
/// and <c>dotquay graph</c>, which wants the failure described well enough for a human to pick a way
/// out of it (SPEC.md §18).
/// </summary>
/// <remarks>
/// The rules themselves are unchanged from v0.4 and are deliberately not a general constraint
/// solver: different repositories under one name can never unify; hard pins (<c>rev</c>/<c>branch</c>)
/// only unify with an identical pin and never with a <c>version</c> range; and a set of
/// <c>version</c> ranges unifies to the highest published non-prerelease tag satisfying all of them
/// simultaneously.
/// </remarks>
public sealed class AddonUnifier {
    /// <summary>Cap on how many alternative tags a <see cref="GraphConflictKind.NoCommonVersion"/>
    /// conflict offers. A picker showing every tag a long-lived repo ever published is neither
    /// useful nor cheap to ship through JSON; the highest N are the ones anyone actually chooses
    /// between, and they're ordered newest-first.</summary>
    public const int MaxVersionCandidates = 50;

    private readonly IGitClient _gitClient;
    private readonly AddonResolver _resolver;

    public AddonUnifier(IGitClient gitClient) {
        _gitClient = gitClient;
        _resolver = new AddonResolver(gitClient);
    }

    public async Task<UnificationOutcome> UnifyAsync(
        string name, IReadOnlyList<AddonRequirement> requirements, CancellationToken cancellationToken = default) {
        if (requirements.Count == 0) {
            throw new ArgumentException($"No requirements were collected for '{name}'.", nameof(requirements));
        }

        if (requirements.Count == 1) {
            return await ResolveSingleAsync(name, requirements[0], requirements, cancellationToken);
        }

        // Compared as repositories rather than as strings (v0.15). The same repo written https:// by
        // one requirer and git@ by another is not a conflict, and reporting it as one is the worst
        // kind of error: unactionable, because neither requirer is wrong. GitTransportPreference
        // normally makes the two agree before resolution reaches here; this covers what it declines
        // to rewrite -- an unknown host, or one the project itself describes two ways.
        var distinctRepositories = requirements
            .Select(r => Git.GitUrl.TryParse(r.Spec.Git, out var url) && url is not null ? url.Identity : r.Spec.Git)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinctRepositories.Count > 1) {
            return UnificationOutcome.Failure(await BuildDifferentGitUrlsConflictAsync(name, requirements, cancellationToken));
        }

        // Checked before versions because it is independent of them: two requirers can agree on the
        // exact commit and still be asking for two different folders, and Godot's flat addons/ can
        // only hold one. Unlike a version range there is nothing to narrow, so this is reported
        // rather than guessed.
        var folderConflict = TryBuildFolderConflict(name, requirements);
        if (folderConflict is not null) {
            return UnificationOutcome.Failure(folderConflict);
        }

        var pins = requirements.Where(r => r.Spec.SourceKind != AddonSourceKind.Version).ToList();
        var versions = requirements.Where(r => r.Spec.SourceKind == AddonSourceKind.Version).ToList();

        if (pins.Count > 0) {
            var distinctPins = pins.Select(r => PinKey(r.Spec)).Distinct(StringComparer.Ordinal).ToList();
            if (distinctPins.Count > 1 || versions.Count > 0) {
                return UnificationOutcome.Failure(await BuildDisjointPinsConflictAsync(name, requirements, cancellationToken));
            }

            return await ResolveSingleAsync(name, pins[0], requirements, cancellationToken);
        }

        return await UnifyVersionsAsync(name, versions, requirements, cancellationToken);
    }

    /// <summary>Every requirement is a <c>version</c> range against the same repo — pick the highest
    /// tag satisfying all of them at once (SPEC.md §8).</summary>
    private async Task<UnificationOutcome> UnifyVersionsAsync(
        string name,
        IReadOnlyList<AddonRequirement> versionRequirements,
        IReadOnlyList<AddonRequirement> allRequirements,
        CancellationToken cancellationToken) {
        var parsed = new List<(AddonRequirement Requirement, VersionConstraint Constraint)>();
        foreach (var requirement in versionRequirements) {
            if (!VersionConstraint.TryParse(requirement.Spec.Version!, out var constraint) || constraint is null) {
                return UnificationOutcome.Failure(new GraphConflict(
                    name,
                    GraphConflictKind.InvalidConstraint,
                    $"'{name}': '{requirement.Spec.Version}' (from {Describe(requirement)}) is not a valid version constraint.",
                    allRequirements.Select(r => r.ToGraphRequirement()).ToList(),
                    []));
            }

            parsed.Add((requirement, constraint));
        }

        var gitUrl = versionRequirements[0].Spec.Git;
        // Simplification carried over from v0.4: the effective subtree path comes from whichever
        // requirement happened to be first rather than being cross-checked for agreement — Path is a
        // fact about the repo's own layout, not something two consumers of the same addon should
        // realistically disagree about (SPEC.md §8 scopes this away from being a full solver).
        var effectivePath = versionRequirements[0].Spec.EffectivePath;
        var installAs = versionRequirements[0].Spec.InstallAs;

        // Prerelease tags included: whether one qualifies is the constraints' decision (DESIGN-v0.21
        // §3.4), and a range that names one must be able to select it.
        var tags = await ListVersionTagsAsync(gitUrl, includePrereleases: true, cancellationToken);

        AddonVersion? best = null;
        GitRef? bestRef = null;
        foreach (var (version, gitRef) in tags) {
            if (!parsed.All(p => p.Constraint.IsSatisfiedBy(version))) {
                continue;
            }

            if (best is null || version.CompareTo(best) > 0) {
                best = version;
                bestRef = gitRef;
            }
        }

        if (best is not null && bestRef is not null) {
            return UnificationOutcome.Success(
                new ResolvedAddon(name, gitUrl, bestRef.CommitSha, best.OriginalTag, effectivePath, installAs));
        }

        var candidates = tags
            .Where(t => !t.Version.IsPrerelease)
            .OrderByDescending(t => t.Version)
            .Take(MaxVersionCandidates)
            .Select(t => new GraphCandidate(
                AddonSourceKind.Version,
                t.Version.OriginalTag,
                gitUrl,
                t.Ref.CommitSha,
                parsed.Where(p => p.Constraint.IsSatisfiedBy(t.Version)).Select(p => p.Requirement.Requirer).ToList(),
                SatisfiesAll: false))
            .ToList();

        return UnificationOutcome.Failure(new GraphConflict(
            name,
            GraphConflictKind.NoCommonVersion,
            $"'{name}': no tag satisfies every requested version constraint at once: "
            + string.Join("; ", versionRequirements.Select(r => $"{Describe(r)} wants {r.Spec.Version}"))
            + $". Add an [overrides.{name}] entry in the project manifest to force one.",
            allRequirements.Select(r => r.ToGraphRequirement()).ToList(),
            candidates));
    }

    /// <summary>
    /// Reports a disagreement about <c>install_as</c> (SPEC.md §7.1), or null when every requirer
    /// wants the same folder.
    /// </summary>
    /// <remarks>
    /// Each candidate names one proposed folder and, in <see cref="GraphCandidate.SatisfiedRequirers"/>,
    /// who asked for it — which is the whole basis for choosing, since the "right" folder is usually
    /// whichever one some addon hardcodes in a <c>preload</c>. Candidates carry the requirer's own
    /// source spec too, so accepting one maps straight onto a complete <c>[overrides]</c> entry.
    /// </remarks>
    private static GraphConflict? TryBuildFolderConflict(string name, IReadOnlyList<AddonRequirement> requirements) {
        var byFolder = requirements
            .GroupBy(r => r.Spec.EffectiveFolder, StringComparer.Ordinal)
            .ToList();

        if (byFolder.Count < 2) {
            return null;
        }

        var candidates = byFolder
            .Select(group => new GraphCandidate(
                group.First().Spec.SourceKind,
                AddonRequirement.ConstraintText(group.First().Spec),
                group.First().Spec.Git,
                CommitSha: null,
                group.Select(r => r.Requirer).ToList(),
                SatisfiesAll: false,
                InstallAs: group.Key))
            .ToList();

        return new GraphConflict(
            name,
            GraphConflictKind.DisagreeingInstallFolders,
            $"'{name}' is wanted in different folders under addons/ and only one copy can exist: "
            + string.Join("; ", byFolder.Select(g =>
                $"{string.Join(", ", g.Select(r => Describe(r)))} want{(g.Count() == 1 ? "s" : "")} addons/{g.Key}"))
            + $". Add an [overrides.{name}] entry with install_as to force one.",
            requirements.Select(r => r.ToGraphRequirement()).ToList(),
            candidates);
    }

    private async Task<GraphConflict> BuildDifferentGitUrlsConflictAsync(
        string name, IReadOnlyList<AddonRequirement> requirements, CancellationToken cancellationToken) =>
        new(
            name,
            GraphConflictKind.DifferentGitUrls,
            $"'{name}' is required from different git repositories and can't be unified: "
            + string.Join("; ", requirements.Select(r => $"{Describe(r)} wants {r.Spec.Git}"))
            + $". Add an [overrides.{name}] entry in the project manifest to force one.",
            requirements.Select(r => r.ToGraphRequirement()).ToList(),
            await BuildPerRequirementCandidatesAsync(requirements, cancellationToken));

    private async Task<GraphConflict> BuildDisjointPinsConflictAsync(
        string name, IReadOnlyList<AddonRequirement> requirements, CancellationToken cancellationToken) =>
        new(
            name,
            GraphConflictKind.DisjointPins,
            $"'{name}' has conflicting pins that can't be unified: "
            + string.Join("; ", requirements.Select(r => $"{Describe(r)} wants {PinDescription(r.Spec)}"))
            + $". Add an [overrides.{name}] entry in the project manifest to force one.",
            requirements.Select(r => r.ToGraphRequirement()).ToList(),
            await BuildPerRequirementCandidatesAsync(requirements, cancellationToken));

    /// <summary>
    /// For conflicts where there is no shared version axis to enumerate (different repos, or
    /// incompatible pins), the candidates on offer are simply "whatever each requirer asked for" —
    /// each one resolved to a real commit so a UI can show what picking it would actually install.
    /// A candidate that fails to resolve (dead branch, deleted tag) is still listed, with a null
    /// <see cref="GraphCandidate.CommitSha"/>: knowing an option exists but is broken beats silently
    /// hiding it.
    /// </summary>
    private async Task<IReadOnlyList<GraphCandidate>> BuildPerRequirementCandidatesAsync(
        IReadOnlyList<AddonRequirement> requirements, CancellationToken cancellationToken) {
        var candidates = new List<GraphCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var requirement in requirements) {
            var key = requirement.Spec.Git + " " + PinKey(requirement.Spec);
            if (!seen.Add(key)) {
                continue;
            }

            var satisfied = requirements
                .Where(r => string.Equals(r.Spec.Git, requirement.Spec.Git, StringComparison.Ordinal)
                    && string.Equals(PinKey(r.Spec), PinKey(requirement.Spec), StringComparison.Ordinal))
                .Select(r => r.Requirer)
                .ToList();

            candidates.Add(new GraphCandidate(
                requirement.Spec.SourceKind,
                AddonRequirement.ConstraintText(requirement.Spec),
                requirement.Spec.Git,
                await TryResolveShaAsync(requirement.Spec, cancellationToken),
                satisfied,
                SatisfiesAll: satisfied.Count == requirements.Count));
        }

        return candidates;
    }

    private async Task<string?> TryResolveShaAsync(AddonSpec spec, CancellationToken cancellationToken) {
        try {
            return (await _resolver.ResolveAsync(spec, cancellationToken)).ResolvedSha;
        } catch (AddonResolutionException) {
            return null;
        } catch (Git.GitCommandException) {
            return null;
        }
    }

    /// <summary>A single requirement still goes through <see cref="AddonResolver"/>, but a resolution
    /// failure becomes a conflict rather than an exception so that one unresolvable addon doesn't
    /// abort the whole graph report.</summary>
    private async Task<UnificationOutcome> ResolveSingleAsync(
        string name,
        AddonRequirement chosen,
        IReadOnlyList<AddonRequirement> allRequirements,
        CancellationToken cancellationToken) {
        try {
            return UnificationOutcome.Success(await _resolver.ResolveAsync(chosen.Spec, cancellationToken));
        } catch (AddonResolutionException ex) {
            var candidates = chosen.Spec.SourceKind == AddonSourceKind.Version
                ? (await ListVersionTagsAsync(chosen.Spec.Git, includePrereleases: false, cancellationToken))
                    .OrderByDescending(t => t.Version)
                    .Take(MaxVersionCandidates)
                    .Select(t => new GraphCandidate(
                        AddonSourceKind.Version, t.Version.OriginalTag, chosen.Spec.Git, t.Ref.CommitSha, [], SatisfiesAll: false))
                    .ToList()
                : [];

            return UnificationOutcome.Failure(new GraphConflict(
                name,
                GraphConflictKind.NoCommonVersion,
                ex.Message,
                allRequirements.Select(r => r.ToGraphRequirement()).ToList(),
                candidates));
        }
    }

    private async Task<IReadOnlyList<(AddonVersion Version, GitRef Ref)>> ListVersionTagsAsync(
        string gitUrl, bool includePrereleases, CancellationToken cancellationToken) {
        var refs = await _gitClient.ListRemoteRefsAsync(gitUrl, cancellationToken);

        var tags = new List<(AddonVersion, GitRef)>();
        foreach (var gitRef in refs) {
            if (gitRef.Kind == GitRefKind.Tag
                && AddonVersion.TryParse(gitRef.Name, out var version)
                && version is not null
                && (includePrereleases || !version.IsPrerelease)) {
                tags.Add((version, gitRef));
            }
        }

        return tags;
    }

    private static string PinKey(AddonSpec spec) => spec.SourceKind switch {
        AddonSourceKind.Rev => $"rev:{spec.Rev}",
        AddonSourceKind.Branch => $"branch:{spec.Branch}",
        _ => $"version:{spec.Version}",
    };

    private static string PinDescription(AddonSpec spec) => spec.SourceKind switch {
        AddonSourceKind.Rev => $"rev {spec.Rev}",
        AddonSourceKind.Branch => $"branch {spec.Branch}",
        _ => $"version {spec.Version}",
    };

    private static string Describe(AddonRequirement requirement) =>
        requirement.Requirer == GraphConflict.ProjectRequirerName ? "the project" : $"'{requirement.Requirer}'";
}
