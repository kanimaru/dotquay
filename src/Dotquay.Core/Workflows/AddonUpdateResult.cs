using Dotquay.Core.Lockfile;

namespace Dotquay.Core.Workflows;

/// <summary>One addon's outcome from <see cref="DependencyWorkflow.UpdateAsync"/> — pairs the
/// freshly re-resolved <see cref="Locked"/> entry with what was locked before, so the CLI can print
/// "unchanged" versus "X -&gt; Y" instead of re-diffing the lockfile itself.</summary>
public sealed record AddonUpdateResult(string Name, string? PreviousResolved, LockedAddon Locked) {
    /// <summary>False only when the resolver picked the exact same commit that was already locked
    /// (e.g. a <c>version</c> constraint with no newer matching tag). True for a real move to a new
    /// commit, and also true the first time a manifest-declared addon gets locked at all
    /// (<see cref="PreviousResolved"/> is null) — going from "unlocked" to "locked" is itself a
    /// change worth reporting.</summary>
    public bool Changed => PreviousResolved != Locked.Resolved;
}
