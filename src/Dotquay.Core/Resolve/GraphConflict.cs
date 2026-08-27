using Dotquay.Core.Manifest;

namespace Dotquay.Core.Resolve;

/// <summary>Why a set of requirements on one addon name could not be unified down to a single
/// commit (SPEC.md §8: "constraints that cannot be unified ... are a hard error").</summary>
public enum GraphConflictKind
{
    /// <summary>Two requirers named the same addon but different repositories — there is no version
    /// to pick, because they aren't the same package.</summary>
    DifferentGitUrls,

    /// <summary>Two incompatible hard pins (<c>rev</c>/<c>branch</c>), or a hard pin colliding with a
    /// <c>version</c> range. Unification has nothing to intersect.</summary>
    DisjointPins,

    /// <summary>Every requirement is a <c>version</c> range against the same repo, but no published
    /// tag satisfies all of them at once.</summary>
    NoCommonVersion,

    /// <summary>A requirement's <c>version</c> text isn't a valid constraint at all — a malformed
    /// manifest somewhere in the graph, not a genuine disagreement.</summary>
    InvalidConstraint,

    /// <summary>Two requirers agree on the commit but want it installed under different folder names
    /// (<c>install_as</c>, SPEC.md §7.1). Godot's flat <c>addons/</c> holds one copy, so this cannot
    /// be split the way a version range can be narrowed — and guessing is worse than asking, because
    /// addons <c>preload</c> each other by absolute path and the wrong folder breaks them at parse
    /// time rather than at runtime.</summary>
    DisagreeingInstallFolders,
}

/// <summary>One requirer's demand on an addon name, flattened for reporting. <see cref="Requirer"/>
/// is either <see cref="GraphConflict.ProjectRequirerName"/> or the name of the addon whose package
/// manifest (SPEC.md §7.2) declared the dependency.</summary>
public sealed record GraphRequirement(string Requirer, AddonSourceKind Kind, string Constraint, string Git);

/// <summary>A concrete resolution a user could pick to settle a <see cref="GraphConflict"/> — shaped
/// so that it maps 1:1 onto an <c>[overrides.&lt;name&gt;]</c> entry (SPEC.md §7.1/§8: the project's
/// override always wins). <see cref="SatisfiedRequirers"/> lets a UI show which requirers each
/// candidate would keep happy, which is the whole point of offering a picker instead of just an
/// error string.</summary>
/// <param name="InstallAs">Folder to install into, for a
/// <see cref="GraphConflictKind.DisagreeingInstallFolders"/> conflict where the disagreement is about
/// the destination rather than the commit. Null everywhere else, meaning "the addon's own name".</param>
public sealed record GraphCandidate(
    AddonSourceKind Kind,
    string Value,
    string Git,
    string? CommitSha,
    IReadOnlyList<string> SatisfiedRequirers,
    bool SatisfiesAll,
    string? InstallAs = null);

/// <summary>
/// A unification failure captured as data rather than thrown as an exception. <c>install</c>/
/// <c>update</c> still treat this as a hard error (<see cref="AddonResolutionException"/> is built
/// from <see cref="Message"/>), but <c>dotquay graph</c> — and therefore the Godot editor plugin's
/// conflict picker (SPEC.md §18) — needs the structured form: who asked for what, and which
/// concrete versions are actually on offer.
/// </summary>
public sealed record GraphConflict(
    string AddonName,
    GraphConflictKind Kind,
    string Message,
    IReadOnlyList<GraphRequirement> Requirements,
    IReadOnlyList<GraphCandidate> Candidates)
{
    /// <summary>The synthetic requirer name used for the project's own top-level
    /// <c>[addons.*]</c> entries, as opposed to a dependency declared by another addon.</summary>
    public const string ProjectRequirerName = "<project>";
}
