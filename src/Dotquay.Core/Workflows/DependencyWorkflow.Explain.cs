using System.Text;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Patch;
using Dotquay.Core.Sync;

namespace Dotquay.Core.Workflows;

/// <summary>
/// <c>dotquay status --explain</c>: why an addon reads as modified
/// (docs/DESIGN-v0.12-MODIFIED-REASON.md).
/// </summary>
/// <remarks>
/// <para>
/// The content hash stays the authority. Nothing in this file participates in deciding whether an
/// addon is modified, whether <c>install</c>/<c>update</c> refuses, or what <c>--force</c> does — it
/// reports on a verdict <see cref="AddonStatusEvaluator"/> already reached, on a separate path that
/// only <c>--explain</c> takes. That separation is what makes it safe for the classification to be
/// heuristic (see <see cref="ChangeClassifier"/>).
/// </para>
/// <para>
/// This is the automated form of the diagnostic SPEC.md §7.3 has described in prose since v0.9 —
/// diff the installed tree against a fresh copy of its own locked commit, split artifact from real —
/// and it reuses <see cref="PatchAsync"/>'s established mechanics for it: warm the shared cache,
/// extract the subtree into a diff-only staging folder, compare, delete in a <c>finally</c>.
/// </para>
/// </remarks>
public sealed partial class DependencyWorkflow {
    /// <summary>Distinct from the installer's stage-and-swap target for the same addon: this is a
    /// read-only scratch copy for diffing, following the precedent
    /// <c>&lt;name&gt;-patch-base</c> set in <see cref="PatchAsync"/>.</summary>
    private const string ExplainStagingSuffix = "-explain-base";

    /// <summary>How many changed files a refusal names before pointing at
    /// <c>status --explain</c> for the rest. A refusal is read in a terminal mid-command; past a
    /// handful the list stops answering "what did I change?" and starts hiding the advice under it.</summary>
    private const int MaxListedLocalChanges = 5;

    /// <summary>
    /// <see cref="Status"/>, optionally with an <see cref="AddonExplanation"/> attached to every
    /// modified addon.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With <paramref name="explain"/> false this is exactly <see cref="Status"/> and touches no
    /// network — the synchronous overload remains what <c>graph</c>, <c>doctor</c>, the editor plugin,
    /// and plain <c>dotquay status</c> call. This adds a path; it does not reroute one.
    /// </para>
    /// <para>
    /// Explaining is opt-in because reconstructing the expected tree may fetch. The work is bounded by
    /// the number of <em>modified</em> addons, which in a healthy project is zero — so the flag costs
    /// nothing when there is nothing to explain.
    /// </para>
    /// </remarks>
    public async Task<StatusResult> StatusAsync(
        string projectDirectory, bool explain, CancellationToken cancellationToken = default) {
        var status = Status(projectDirectory);

        if (!explain || status.Addons.All(addon => addon.Kind != AddonStatusKind.Modified)) {
            return status;
        }

        var (lockfile, _) = ReadOrCreateLockfile(projectDirectory);
        var locked = lockfile.Addons.ToDictionary(addon => addon.Name, StringComparer.Ordinal);
        var layout = ReadLayout(projectDirectory);

        var explained = new List<AddonStatusInfo>(status.Addons.Count);

        foreach (var addon in status.Addons) {
            if (addon.Kind != AddonStatusKind.Modified || !locked.TryGetValue(addon.Name, out var entry)) {
                explained.Add(addon);
                continue;
            }

            explained.Add(addon with { Explanation = await ExplainAsync(layout, entry, cancellationToken) });
        }

        return new StatusResult(explained);
    }

    private async Task<AddonExplanation> ExplainAsync(
        ProjectLayout layout, LockedAddon locked, CancellationToken cancellationToken) {
        var installedPath = InstallPaths.For(_fileSystem, layout.ProjectDirectory, locked);
        var baselinePath = _fileSystem.CombinePath(
            layout.ProjectDirectory, ProjectLayout.DotquayDirectory, "staging", locked.Name + ExplainStagingSuffix);

        try {
            var baseline = await BuildBaselineAsync(layout, locked, baselinePath, cancellationToken);

            var changes = TreeComparer.Compare(_fileSystem, baselinePath, installedPath);
            var counts = ChangeCounts.From(changes);

            return new AddonExplanation {
                // Only a real content difference is worth stopping for. Everything else the editor
                // either regenerates or never needed — see ChangeClass.
                Verdict = counts.Content > 0 ? ExplainVerdict.ReviewNeeded : ExplainVerdict.SafeToForce,
                Changes = changes.Take(AddonExplanation.MaxReportedChanges).ToList(),
                Counts = counts,
                BaselineIncludesPatch = baseline.IncludesPatch,
                PatchCouldNotBeApplied = baseline.PatchFailed,
            };
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // Broad on purpose. Offline with a cold cache, a force-pushed-away commit, a repository
            // that moved, a subtree that no longer exists at that SHA — every one of them means "no
            // explanation available", and an honest report of that is the correct output. It must
            // never take the whole `status` down with it, since the other addons are still
            // explainable and the status verdicts themselves were computed before any of this ran.
            return new AddonExplanation {
                Verdict = ExplainVerdict.Unavailable,
                UnavailableReason = ex.Message,
            };
        } finally {
            _fileSystem.DeleteDirectory(baselinePath);
        }
    }

    /// <summary>
    /// The lines a P0-6 refusal appends to name what is modified: the first
    /// <see cref="MaxListedLocalChanges"/> files that differ from <paramref name="locked"/>'s commit,
    /// and a pointer at <c>status --explain</c> when there are more. Empty when there is nothing
    /// trustworthy to list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built on <see cref="ExplainAsync"/>, so "changed" means here exactly what it means to
    /// <c>status --explain</c> — patch-aware baseline included. Editor artifacts are left out: they are
    /// not hashed, so they can never be why an install refused, and naming them would point the reader
    /// at files that are not the problem.
    /// </para>
    /// <para>
    /// Only the folder <paramref name="locked"/> describes can be explained against it. A refusal over
    /// some other folder (a rename's destination already occupied) has no locked tree to compare with,
    /// and an empty answer is better than a list measured against the wrong one. Likewise when the
    /// baseline cannot be rebuilt (offline, commit gone): the refusal still stands on its own.
    /// </para>
    /// </remarks>
    private async Task<string> DescribeLocalChangesAsync(
        string projectDirectory, LockedAddon? locked, string modifiedPath, CancellationToken cancellationToken) {
        if (locked is null
            || !string.Equals(
                InstallPaths.For(_fileSystem, projectDirectory, locked), modifiedPath, StringComparison.Ordinal)) {
            return string.Empty;
        }

        var explanation = await ExplainAsync(ReadLayout(projectDirectory), locked, cancellationToken);

        var hashed = explanation.Changes.Where(change => change.Class != ChangeClass.EditorArtifact).ToList();
        if (explanation.Verdict == ExplainVerdict.Unavailable || hashed.Count == 0) {
            return string.Empty;
        }

        // From the exact counts, not the list: the list is capped at MaxReportedChanges.
        var total = explanation.Counts.Total - explanation.Counts.EditorArtifact;
        var newLine = Environment.NewLine;

        var builder = new StringBuilder();
        builder.Append($"{newLine}  Changed locally ({total} file{(total == 1 ? "" : "s")}):");

        foreach (var change in hashed.Take(MaxListedLocalChanges)) {
            var kind = DescribeChangeKind(change.Kind);
            builder.Append($"{newLine}    {kind,-8} {change.Path}{DescribeChangeClass(change.Class)}");
        }

        if (total > MaxListedLocalChanges) {
            builder.Append(
                $"{newLine}    ... and {total - MaxListedLocalChanges} more. "
                + "Run 'dotquay status --explain' for the full list.");
        }

        builder.Append(newLine).Append(DescribeVerdict(explanation.Counts));
        return builder.ToString();
    }

    /// <summary>
    /// The sentence that answers "do I have to care?": which of the changes are churn nobody made by
    /// hand (whitespace, uid rewrites, line endings) and which are real edits.
    /// </summary>
    /// <remarks>
    /// Derived from the same counts <c>status --explain</c> reports, with editor artifacts left out for
    /// the reason the list leaves them out. Advice only — the refusal itself has already been decided.
    /// </remarks>
    private static string DescribeVerdict(ChangeCounts counts) {
        var newLine = Environment.NewLine;

        if (counts.Content > 0) {
            var one = counts.Content == 1;
            return $"  {counts.Content} real content change{(one ? "" : "s")}: --force would discard "
                + $"{(one ? "it" : "them")}. Capture {(one ? "it" : "them")} with 'dotquay patch' first if "
                + $"you want to keep {(one ? "it" : "them")}.";
        }

        var parts = new List<string>(3);
        AppendCount(parts, counts.WhitespaceOnly, "whitespace-only change", "whitespace-only changes");
        AppendCount(parts, counts.UidOnly, "uid-only rewrite", "uid-only rewrites");
        AppendCount(parts, counts.LineEndingsOnly, "line-ending change", "line-ending changes");

        return $"  Only {string.Join(", ", parts)} — no real edits (an editor or formatter probably "
            + $"rewrote them).{newLine}  'dotquay update --force' is safe.";
    }

    private static void AppendCount(List<string> parts, int count, string singular, string plural) {
        if (count > 0) {
            parts.Add($"{count} {(count == 1 ? singular : plural)}");
        }
    }

    private static string DescribeChangeKind(ChangeKind kind) {
        return kind switch {
            ChangeKind.Added => "added",
            ChangeKind.Removed => "removed",
            _ => "changed",
        };
    }

    /// <summary>Flags the changes nobody made by hand, so the reader can tell editor churn from their
    /// own work at a glance. A real content change carries no suffix: it is the default reading.</summary>
    private static string DescribeChangeClass(ChangeClass @class) {
        return @class switch {
            ChangeClass.UidOnly => "  (uid only)",
            ChangeClass.WhitespaceOnly => "  (whitespace only)",
            ChangeClass.LineEndingsOnly => "  (line endings only)",
            _ => string.Empty,
        };
    }

    /// <param name="IncludesPatch">The addon's captured patch was re-applied onto the baseline.</param>
    /// <param name="PatchFailed">A patch was declared and could not be reconstructed — distinct from
    /// there being no patch at all, which is the ordinary case and worth saying nothing about.</param>
    private readonly record struct Baseline(bool IncludesPatch, bool PatchFailed);

    /// <summary>
    /// Materialises what <em>should</em> be installed, into <paramref name="baselinePath"/>.
    /// </summary>
    /// <remarks>
    /// Built the way <see cref="AddonInstaller"/> would build it, so "expected" means the same thing
    /// here as it does to the installer. That includes re-applying the patch: <see cref="PatchAsync"/>
    /// records the <em>patched</em> tree as <c>content_hash</c>, so diffing a patched addon against
    /// the pristine subtree would report the user's own captured patch back at them as unexplained
    /// modification — the single most confusing answer this command could give.
    /// </remarks>
    private async Task<Baseline> BuildBaselineAsync(
        ProjectLayout layout, LockedAddon locked, string baselinePath, CancellationToken cancellationToken) {
        var cachedRepoPath = await _repoCache.GetOrFetchAsync(locked.Git, locked.Resolved, cancellationToken);
        SubtreeExtractor.Extract(_fileSystem, cachedRepoPath, locked.Path, baselinePath);

        if (locked is not { PatchHash: not null, PatchBaseSha: not null }) {
            // No patch to include and none missing — the ordinary case, and not worth a caveat.
            return new Baseline(IncludesPatch: false, PatchFailed: false);
        }

        var patchFilePath = layout.PatchFilePath(_fileSystem, locked.Name);
        if (!_fileSystem.FileExists(patchFilePath)) {
            // The lockfile claims a patch the file no longer backs. Not this command's job to fix that
            // (doctor's territory, and AddonInstaller takes the same line) — but the comparison is
            // measurably incomplete, so say so.
            return new Baseline(IncludesPatch: false, PatchFailed: true);
        }

        try {
            var conflicts = await new PatchReapplier(_fileSystem, _repoCache, _patchTool).ReapplyAsync(
                locked.Git,
                locked.PatchBaseSha,
                locked.Path,
                _fileSystem.ReadAllText(patchFilePath),
                baselinePath,
                cancellationToken);

            if (conflicts.Count == 0) {
                return new Baseline(IncludesPatch: true, PatchFailed: false);
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // Falls through to the pristine baseline below, exactly as a conflict does.
        }

        // A conflicted re-apply leaves <<<<<<< markers in the staged tree (SPEC.md §10 step 3), which
        // would make every touched file read as a Content change and the verdict a confident lie.
        // Re-extract the pristine subtree and report the failure, so the caller can say the patch is
        // missing from the comparison rather than quietly measuring against garbage.
        SubtreeExtractor.Extract(_fileSystem, cachedRepoPath, locked.Path, baselinePath);
        return new Baseline(IncludesPatch: false, PatchFailed: true);
    }
}
