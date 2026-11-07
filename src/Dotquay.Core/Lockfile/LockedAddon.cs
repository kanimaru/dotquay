namespace Dotquay.Core.Lockfile;

/// <summary>One resolved <c>[[addon]]</c> entry (SPEC.md §7.3).</summary>
/// <remarks>
/// A <c>record</c> specifically so callers can say <c>locked with { Dependencies = ... }</c>. It was
/// a class, which forced two hand-written copy helpers that listed every property — and when
/// <see cref="Folder"/> was added, both silently dropped it, so an addon installed into the right
/// folder was recorded as if it were in the default one. Every consumer then disagreed with disk:
/// <c>status</c> called it missing and <c>doctor</c> called its folder an orphan. Copying by hand is
/// exactly the wrong tool for a type that gains fields.
/// </remarks>
public sealed record LockedAddon {
    public required string Name { get; init; }

    public required string Git { get; init; }

    /// <summary>Always a full commit SHA — never a short SHA or a branch name
    /// (SPEC.md §7.3, enforced by <see cref="Lockfile.LockfileReader"/>).</summary>
    public required string Resolved { get; init; }

    /// <summary>Display only; a pseudo-version for untagged repos, e.g.
    /// <c>"0.0.0-20260821-9f2c1ab"</c> (SPEC.md §6).</summary>
    public string? Version { get; init; }

    /// <summary>The subtree extracted from the addon's <em>own</em> repo — not where it is installed.
    /// See <see cref="Folder"/> for that.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The folder under <c>addons/</c> this addon is installed into (SPEC.md §7.1's <c>install_as</c>).
    /// Null means the default, which is <see cref="Name"/> — so every lockfile written before this
    /// field existed still reads correctly.
    /// </summary>
    /// <remarks>
    /// Recorded rather than recomputed from the manifest because <c>install</c> must be able to find,
    /// hash, and replace an installed addon from the lockfile alone (SPEC.md §5) — including for a
    /// transitive addon that appears in no manifest the project owns. It is also what lets
    /// <c>update</c> notice that a folder name changed and clean up the old directory instead of
    /// leaving it behind to satisfy stale <c>preload</c> paths.
    /// </remarks>
    public string? Folder { get; init; }

    /// <summary>Where this addon actually lives under <c>addons/</c>.</summary>
    public string EffectiveFolder => Folder ?? Name;

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
