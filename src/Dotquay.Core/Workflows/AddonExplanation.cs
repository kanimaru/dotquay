using Dotquay.Core.Sync;

namespace Dotquay.Core.Workflows;

/// <summary>What <c>dotquay status --explain</c> concluded about one modified addon.</summary>
public enum ExplainVerdict {
    /// <summary>Every difference is benign — a force-update would discard nothing of
    /// value.</summary>
    SafeToForce,

    /// <summary>At least one real content difference. Look before forcing.</summary>
    ReviewNeeded,

    /// <summary>The expected tree could not be reconstructed; see
    /// <see cref="AddonExplanation.UnavailableReason"/>.</summary>
    Unavailable,
}

/// <summary>
/// Why one addon reads as modified (docs/DESIGN-v0.12-MODIFIED-REASON.md): what differs from the
/// locked commit, and whether any of it is worth keeping.
/// </summary>
/// <remarks>
/// Attached to <see cref="AddonStatusInfo"/> only when <c>--explain</c> asked for it. It reports on
/// a verdict <see cref="AddonStatusEvaluator"/> already reached and never influences one.
/// </remarks>
public sealed record AddonExplanation {
    /// <summary>
    /// How many changes are reported in <see cref="Changes"/> at most.
    /// </summary>
    /// <remarks>
    /// An addon that drifted catastrophically (a wrong-cased folder Godot has been rewriting for
    /// months) otherwise produces an unreadable terminal wall and an unbounded JSON document. The
    /// counts stay exact regardless, so the cap costs no accuracy — only detail, and only past the
    /// point where detail stopped being read.
    /// </remarks>
    public const int MaxReportedChanges = 100;

    public required ExplainVerdict Verdict { get; init; }

    /// <summary>The differences, most significant first, truncated at
    /// <see cref="MaxReportedChanges"/>.</summary>
    public IReadOnlyList<TreeChange> Changes { get; init; } = [];

    /// <summary>Exact totals per class, over every difference — not just the reported
    /// ones.</summary>
    public ChangeCounts Counts { get; init; } = new();

    /// <summary>Set only when <see cref="Verdict"/> is
    /// <see cref="ExplainVerdict.Unavailable"/>.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>
    /// Whether the baseline had the addon's captured patch re-applied.
    /// </summary>
    /// <remarks>
    /// <c>dotquay patch</c> records the <em>patched</em> tree as <c>content_hash</c>, so for a
    /// patched addon the pristine subtree is the wrong thing to compare against — it would report
    /// the user's own patch as unexplained modification. Surfaced because a reader deciding whether
    /// to trust the list needs to know which tree it was measured from.
    /// </remarks>
    public bool BaselineIncludesPatch { get; init; }

    /// <summary>
    /// A patch was declared for this addon and could not be reconstructed, so the listed changes
    /// include the patch's own edits.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="BaselineIncludesPatch"/> being false, which is also what an
    /// ordinary unpatched addon looks like. Only this flag means the comparison is incomplete — and
    /// saying so on every unpatched addon would train the reader to skip the line that matters.
    /// </remarks>
    public bool PatchCouldNotBeApplied { get; init; }

    public bool IsTruncated => Counts.Total > Changes.Count;
}

/// <summary>Exact per-class totals, unaffected by
/// <see cref="AddonExplanation.MaxReportedChanges"/>.</summary>
public sealed record ChangeCounts {
    public int Content { get; init; }

    public int UidOnly { get; init; }

    public int WhitespaceOnly { get; init; }

    public int LineEndingsOnly { get; init; }

    public int EditorArtifact { get; init; }

    public int Total => Content + UidOnly + WhitespaceOnly + LineEndingsOnly + EditorArtifact;

    public static ChangeCounts From(IEnumerable<TreeChange> changes) {
        var counts = new Dictionary<ChangeClass, int>();

        foreach (var change in changes) {
            counts[change.Class] = counts.GetValueOrDefault(change.Class) + 1;
        }

        return new ChangeCounts {
            Content = counts.GetValueOrDefault(ChangeClass.Content),
            UidOnly = counts.GetValueOrDefault(ChangeClass.UidOnly),
            WhitespaceOnly = counts.GetValueOrDefault(ChangeClass.WhitespaceOnly),
            LineEndingsOnly = counts.GetValueOrDefault(ChangeClass.LineEndingsOnly),
            EditorArtifact = counts.GetValueOrDefault(ChangeClass.EditorArtifact),
        };
    }
}
