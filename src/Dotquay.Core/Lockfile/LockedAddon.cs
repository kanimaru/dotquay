namespace Dotquay.Core.Lockfile;

/// <summary>One resolved <c>[[addon]]</c> entry (SPEC.md §7.3).</summary>
public sealed class LockedAddon
{
    public required string Name { get; init; }

    public required string Git { get; init; }

    /// <summary>Always a full commit SHA — never a short SHA or a branch name
    /// (SPEC.md §7.3, enforced by <see cref="Lockfile.LockfileReader"/>).</summary>
    public required string Resolved { get; init; }

    /// <summary>Display only; a pseudo-version for untagged repos, e.g.
    /// <c>"0.0.0-20260821-9f2c1ab"</c> (SPEC.md §6).</summary>
    public string? Version { get; init; }

    public required string Path { get; init; }

    /// <summary>Hash of the installed tree, for dirty detection (SPEC.md §9 step 4, requirement P0-6).</summary>
    public string? ContentHash { get; init; }

    /// <summary>Present only if the addon has a captured patch (SPEC.md §10).</summary>
    public string? PatchHash { get; init; }

    /// <summary>The <see cref="Resolved"/> SHA the currently-active patch was captured against —
    /// present exactly when <see cref="PatchHash"/> is (SPEC.md §10 step 3). Stays fixed across a
    /// clean <c>update</c> (only <see cref="Resolved"/>/<see cref="ContentHash"/> advance); only
    /// changes when the user re-runs <c>dotquay patch &lt;name&gt;</c>, e.g. after resolving a
    /// conflict. This is what lets patch re-apply reconstruct "ours" (pristine base at the recorded
    /// SHA + the patch file) independently of whatever the addon's new <see cref="Resolved"/> SHA
    /// is.</summary>
    public string? PatchBaseSha { get; init; }

    /// <summary>Graph edges to other locked addon names — <c>install</c> replays these without
    /// re-reading remote package manifests (SPEC.md §8).</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = new List<string>();
}
