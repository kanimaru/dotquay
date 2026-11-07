using System.Diagnostics.CodeAnalysis;

namespace Dotquay.Core.Resolve;

/// <summary>
/// The result of unifying every requirement on one addon name (SPEC.md §8): either exactly one
/// commit, or a <see cref="GraphConflict"/> describing why not. Deliberately a value rather than an
/// exception — the same unification runs behind <c>install</c>/<c>update</c> (which turn a conflict
/// into a hard <see cref="AddonResolutionException"/>) and behind <c>dotquay graph</c> (which
/// reports it as data so the editor plugin can offer a picker, SPEC.md §18).
/// </summary>
public sealed record UnificationOutcome {
    private UnificationOutcome(ResolvedAddon? resolved, GraphConflict? conflict) {
        Resolved = resolved;
        Conflict = conflict;
    }

    public ResolvedAddon? Resolved { get; }

    public GraphConflict? Conflict { get; }

    [MemberNotNullWhen(true, nameof(Resolved))]
    [MemberNotNullWhen(false, nameof(Conflict))]
    public bool IsResolved => Conflict is null;

    public static UnificationOutcome Success(ResolvedAddon resolved) => new(resolved, null);

    public static UnificationOutcome Failure(GraphConflict conflict) => new(null, conflict);

    /// <summary>Restores the pre-v0.5 behaviour for callers that must fail hard (SPEC.md §8:
    /// unresolvable constraints "are a hard error").</summary>
    public ResolvedAddon ResolvedOrThrow() =>
        IsResolved ? Resolved : throw new AddonResolutionException(Conflict.Message);
}
