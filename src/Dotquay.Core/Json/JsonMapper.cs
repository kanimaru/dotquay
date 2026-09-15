using Dotquay.Core.Graph;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;

namespace Dotquay.Core.Json;

/// <summary>
/// The one place domain models become wire payloads (SPEC.md §17). Every enum-to-string mapping is
/// spelled out here rather than delegated to a naming convention, so the JSON contract can only
/// change by editing this file — a renamed C# enum member is a compile error here, not a silent break
/// in the Godot plugin.
/// </summary>
public static class JsonMapper
{
    public static GraphJson ToJson(GraphReport report) => new()
    {
        Source = report.Source switch
        {
            GraphSource.Lockfile => "lockfile",
            GraphSource.Resolved => "resolved",
            _ => "unknown",
        },
        ProjectDirectory = report.ProjectDirectory,
        Roots = report.RootNames,
        Addons = report.Addons.Select(ToJson).ToList(),
        Edges = report.Edges.Select(e => new GraphEdgeJson(e.From, e.To)).ToList(),
        Dotnet = report.Dotnet.Select(ToJson).ToList(),
        Conflicts = report.Conflicts.Select(ToJson).ToList(),
        Overrides = report.Overrides,
        HasConflicts = report.Conflicts.Count > 0,
    };

    public static GraphNodeJson ToJson(GraphNode node) => new()
    {
        Name = node.Name,
        Git = node.Git,
        Version = node.Version,
        Resolved = node.ResolvedSha,
        ShortSha = ShortSha(node.ResolvedSha),
        Path = node.Path,
        Folder = node.Folder,
        Origin = node.Origin switch
        {
            GraphNodeOrigin.Root => "root",
            GraphNodeOrigin.Transitive => "transitive",
            GraphNodeOrigin.Orphan => "orphan",
            _ => "unknown",
        },
        SourceKind = node.SourceKind is null ? null : ToWire(node.SourceKind.Value),
        Constraint = node.Constraint,
        IsOverridden = node.IsOverridden,
        IsPatched = node.IsPatched,
        Status = node.Status is null ? null : ToWire(node.Status.Value),
        Dependencies = node.Dependencies,
        Requirements = node.Requirements.Select(ToJson).ToList(),
    };

    public static RequirementJson ToJson(GraphRequirement requirement) =>
        new(requirement.Requirer, ToWire(requirement.Kind), requirement.Constraint, requirement.Git);

    public static CandidateJson ToJson(GraphCandidate candidate) => new()
    {
        Kind = ToWire(candidate.Kind),
        Value = candidate.Value,
        Git = candidate.Git,
        Resolved = candidate.CommitSha,
        Satisfies = candidate.SatisfiedRequirers,
        SatisfiesAll = candidate.SatisfiesAll,
        InstallAs = candidate.InstallAs,
    };

    public static ConflictJson ToJson(GraphConflict conflict) => new()
    {
        Name = conflict.AddonName,
        Kind = conflict.Kind switch
        {
            GraphConflictKind.DifferentGitUrls => "different_git_urls",
            GraphConflictKind.DisjointPins => "disjoint_pins",
            GraphConflictKind.NoCommonVersion => "no_common_version",
            GraphConflictKind.InvalidConstraint => "invalid_constraint",
            GraphConflictKind.DisagreeingInstallFolders => "disagreeing_install_folders",
            _ => "unknown",
        },
        Message = conflict.Message,
        Requirements = conflict.Requirements.Select(ToJson).ToList(),
        Candidates = conflict.Candidates.Select(ToJson).ToList(),
    };

    public static DotnetJson ToJson(GraphDotnetNode node) => new()
    {
        Name = node.Name,
        Constraint = node.Constraint,
        Resolved = node.Resolved,
        DeclaredByProject = node.DeclaredByProject,
    };

    public static ListJson ToListJson(IReadOnlyList<LockedAddon> addons, IReadOnlyList<LockedDotnet> dotnet) => new()
    {
        Addons = addons
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => new ListAddonJson
            {
                Name = a.Name,
                Version = a.Version,
                Resolved = a.Resolved,
                ShortSha = ShortSha(a.Resolved)!,
                Git = a.Git,
                IsPatched = a.PatchHash is not null,
            })
            .ToList(),
        Dotnet = dotnet
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => new DotnetJson
            {
                Name = d.Name,
                Constraint = null,
                Resolved = d.Resolved,
                DeclaredByProject = true,
                IncludeAssets = d.Assets.Include,
                ExcludeAssets = d.Assets.Exclude,
                PrivateAssets = d.Assets.Private,
            })
            .ToList(),
    };

    public static StatusJson ToJson(StatusResult status) => new()
    {
        Addons = status.Addons.Select(a => new StatusEntryJson(a.Name, ToWire(a.Kind), ToJson(a.Explanation))).ToList(),
        // A linked addon counts as clean: it is exactly where its owner put it, and nothing about it
        // needs fixing (SPEC.md §7.4).
        AllClean = status.Addons.All(
            a => a.Kind is AddonStatusKind.Clean or AddonStatusKind.PatchedAsExpected or AddonStatusKind.Linked),
    };

    public static OutdatedJson ToJson(OutdatedResult outdated)
    {
        var addons = outdated.Addons
            .Select(a => new OutdatedEntryJson(a.Name, a.CurrentDisplay, a.LatestDisplay, a.IsOutdated))
            .ToList();
        var dotnet = outdated.Dotnet
            .Select(d => new OutdatedEntryJson(d.Name, d.CurrentVersion, d.LatestVersion, d.IsOutdated))
            .ToList();

        return new OutdatedJson
        {
            Addons = addons,
            Dotnet = dotnet,
            OutdatedCount = addons.Count(a => a.IsOutdated) + dotnet.Count(d => d.IsOutdated),
        };
    }

    public static DoctorJson ToJson(DoctorResult doctor) => new()
    {
        Issues = doctor.Issues.Select(i => new DoctorIssueJson(ToWire(i.Kind), i.Message)).ToList(),
        Healthy = doctor.Issues.Count == 0,
        Trust = new DoctorTrustJson
        {
            TrustedHosts = doctor.Trust.TrustedHosts,
            AdoptedSources = doctor.Trust.AdoptedSources
                .Select(s => new DoctorAdoptedSourceJson(s.Name, s.Url, s.DeclaredByAddon))
                .ToList(),
        },
    };

    public static OperationJson ToJson(UpdateResult update, IReadOnlyList<string>? messages = null) => new()
    {
        Changes = update.Addons
            .Select(a => new ChangeJson
            {
                Name = a.Name,
                Kind = ChangeKind.Addon,
                Action = a.PreviousResolved is null ? ChangeAction.Added
                    : a.PreviousResolved == a.Locked.Resolved ? ChangeAction.Unchanged
                    : ChangeAction.Updated,
                From = ShortSha(a.PreviousResolved),
                To = a.Locked.Version ?? ShortSha(a.Locked.Resolved),
            })
            .Concat(update.Dotnet.Select(d => new ChangeJson
            {
                Name = d.Name,
                Kind = ChangeKind.Dotnet,
                Action = d.PreviousResolved is null ? ChangeAction.Added
                    : d.PreviousResolved == d.Locked.Resolved ? ChangeAction.Unchanged
                    : ChangeAction.Updated,
                From = d.PreviousResolved,
                To = d.Locked.Resolved,
            }))
            .ToList(),
        Messages = messages ?? [],
    };

    public static OperationJson ToJson(InstallResult install, IReadOnlyList<string>? messages = null) => new()
    {
        Changes = install.Addons
            .Select(a => new ChangeJson
            {
                Name = a.Name,
                Kind = ChangeKind.Addon,
                Action = ChangeAction.Installed,
                From = null,
                To = a.Version ?? ShortSha(a.Resolved),
            })
            .Concat(install.Dotnet.Select(d => new ChangeJson
            {
                Name = d.Name,
                Kind = ChangeKind.Dotnet,
                Action = ChangeAction.Installed,
                From = null,
                To = d.Resolved,
            }))
            .ToList(),
        Messages = messages ?? [],
    };

    /// <summary>
    /// Maps <c>add --preview</c>'s resolve into its wire payload (SPEC.md §6, §11.3).
    /// </summary>
    /// <remarks>
    /// The two convenience booleans are computed here rather than in the workflow for the same reason
    /// <see cref="GraphJson.HasConflicts"/> is: they are statements about how to present the payload,
    /// not facts the resolver discovered, and a UI that recomputed them could disagree with one that
    /// read them.
    /// </remarks>
    public static AddPreviewJson ToJson(AddPreview preview) => new()
    {
        Name = preview.Name,
        Git = preview.Git,
        Addons = preview.Addons
            .Select(a => new PreviewAddonJson
            {
                Name = a.Name,
                Git = a.Git,
                Version = a.Version,
                Resolved = a.Resolved,
                ShortSha = ShortSha(a.Resolved) ?? a.Resolved,
                Folder = a.Folder,
                IsNew = a.IsNew,
                PreviousResolved = a.PreviousResolved,
                RequiredBy = a.RequiredBy,
            })
            .ToList(),
        Dotnet = preview.Dotnet
            .Select(d => new PreviewDotnetJson
            {
                Name = d.Name,
                Constraint = d.Constraint,
                DeclaredByAddon = d.DeclaredByAddon,
                IsNew = d.IsNew,
            })
            .ToList(),
        SourcesToAdopt = preview.SourcesToAdopt.Select(ToJson).ToList(),
        SourcesNeedingApproval = preview.SourcesNeedingApproval.Select(ToJson).ToList(),
        NeedsApproval = preview.SourcesNeedingApproval.Count > 0,
        IsNoOp = preview.Addons.All(a => !a.IsNew) && preview.Dotnet.All(d => !d.IsNew),
    };

    private static PreviewSourceJson ToJson(PreviewSource source) => new()
    {
        Name = source.Name,
        Url = source.Url,
        DeclaredByAddon = source.DeclaredByAddon,
        IsTrustedHost = source.IsTrustedHost,
        Packages = source.Packages,
        PackageIds = source.PackageIds,
    };

    public static string ToWire(AddonSourceKind kind) => kind switch
    {
        AddonSourceKind.Version => "version",
        AddonSourceKind.Branch => "branch",
        AddonSourceKind.Rev => "rev",
        _ => "unknown",
    };

    /// <summary>Null in, null out — an addon that was not explained carries no explanation, rather
    /// than an empty one a consumer would have to distinguish from "explained, nothing found".</summary>
    public static StatusExplanationJson? ToJson(AddonExplanation? explanation) => explanation is null
        ? null
        : new StatusExplanationJson
        {
            Verdict = ToWire(explanation.Verdict),
            Counts = new StatusChangeCountsJson
            {
                Content = explanation.Counts.Content,
                UidOnly = explanation.Counts.UidOnly,
                LineEndingsOnly = explanation.Counts.LineEndingsOnly,
                EditorArtifact = explanation.Counts.EditorArtifact,
                Total = explanation.Counts.Total,
            },
            Changes = explanation.Changes
                .Select(change => new StatusChangeJson(change.Path, ToWire(change.Kind), ToWire(change.Class)))
                .ToList(),
            UnavailableReason = explanation.UnavailableReason,
            BaselineIncludesPatch = explanation.BaselineIncludesPatch,
            PatchCouldNotBeApplied = explanation.PatchCouldNotBeApplied,
        };

    public static string ToWire(ExplainVerdict verdict) => verdict switch
    {
        ExplainVerdict.SafeToForce => "safe_to_force",
        ExplainVerdict.ReviewNeeded => "review_needed",
        ExplainVerdict.Unavailable => "unavailable",
        _ => "unknown",
    };

    /// <summary>Fully qualified: this type already nests a <c>ChangeKind</c> holding the wire
    /// constants for <see cref="ChangeJson"/>, which is a different thing entirely and wins name
    /// resolution here.</summary>
    public static string ToWire(Sync.ChangeKind kind) => kind switch
    {
        Sync.ChangeKind.Added => "added",
        Sync.ChangeKind.Removed => "removed",
        Sync.ChangeKind.Changed => "changed",
        _ => "unknown",
    };

    public static string ToWire(Sync.ChangeClass @class) => @class switch
    {
        Sync.ChangeClass.EditorArtifact => "editor_artifact",
        Sync.ChangeClass.LineEndingsOnly => "line_endings_only",
        Sync.ChangeClass.UidOnly => "uid_only",
        Sync.ChangeClass.Content => "content",
        _ => "unknown",
    };

    public static string ToWire(AddonStatusKind status) => status switch
    {
        AddonStatusKind.Clean => "clean",
        // Deliberately "patched", not "patched_as_expected": the wire name is what a UI shows on a
        // badge, and the C# name's emphasis reads as an apology in that context.
        AddonStatusKind.PatchedAsExpected => "patched",
        AddonStatusKind.Modified => "modified",
        AddonStatusKind.Missing => "missing",
        AddonStatusKind.Linked => "linked",
        _ => "unknown",
    };

    public static string ToWire(DoctorIssueKind kind) => kind switch
    {
        DoctorIssueKind.GitNotOnPath => "git_not_on_path",
        DoctorIssueKind.DanglingLockfileEntry => "dangling_lockfile_entry",
        DoctorIssueKind.PluginCfgVersionDrift => "plugin_cfg_version_drift",
        DoctorIssueKind.OrphanedAddonFolder => "orphaned_addon_folder",
        DoctorIssueKind.UnreadablePackageManifest => "unreadable_package_manifest",
        DoctorIssueKind.CredentialFilePermissions => "credential_file_permissions",
        DoctorIssueKind.SecretInManifest => "secret_in_manifest",
        DoctorIssueKind.TrustedHostUnusable => "trusted_host_unusable",
        DoctorIssueKind.FragileUidReference => "fragile_uid_reference",
        DoctorIssueKind.AddonFolderCaseMismatch => "addon_folder_case_mismatch",
        DoctorIssueKind.PackageAssetMetadataDrift => "package_asset_metadata_drift",
        DoctorIssueKind.InstallFolderDrift => "install_folder_drift",
        DoctorIssueKind.LinkedProjectMissing => "linked_project_missing",
        DoctorIssueKind.AddonLinkMismatch => "addon_link_mismatch",
        DoctorIssueKind.LocalOverlayNotImported => "local_overlay_not_imported",
        DoctorIssueKind.SshHostNotTrusted => "ssh_host_not_trusted",
        DoctorIssueKind.NuGetSourceMappingDrift => "nuget_source_mapping_drift",
        _ => "unknown",
    };

    /// <summary>Precomputed so every consumer abbreviates a SHA the same way the CLI prints it.</summary>
    public static string? ShortSha(string? sha) =>
        sha is null ? null : sha.Length > 7 ? sha[..7] : sha;

    /// <summary>Wire values for <see cref="ChangeJson.Kind"/>.</summary>
    public static class ChangeKind
    {
        public const string Addon = "addon";
        public const string Dotnet = "dotnet";
    }

    /// <summary>Wire values for <see cref="ChangeJson.Action"/>.</summary>
    public static class ChangeAction
    {
        public const string Added = "added";
        public const string Updated = "updated";
        public const string Removed = "removed";
        public const string Installed = "installed";
        public const string Unchanged = "unchanged";
    }
}
