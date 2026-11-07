using Dotquay.Core.Json;
using Dotquay.Core.Workflows;
using Dotquay.Mcp.Json;

namespace Dotquay.Mcp.Guards;

/// <summary>
/// The new policy (docs/DESIGN-v0.18-MCP.md §5.3): reproduces what the console's red <c>modified</c>
/// line and <c>--explain</c> verdict stop a human from doing by accident when they pass <c>--force</c>.
/// </summary>
/// <remarks>
/// <c>force: true</c> with no <c>confirm_discard</c> is refused whenever any addon is <c>modified</c>
/// with a real content difference (<see cref="ExplainVerdict.ReviewNeeded"/> or
/// <see cref="ExplainVerdict.Unavailable"/> — either way, forcing could discard something).
/// <c>confirm_discard</c> must then name every one of them; a partial list is refused with exactly the
/// names still missing, because naming each one is what proves the agent actually showed each one to
/// the user rather than rubber-stamping the whole set. An addon whose verdict is
/// <see cref="ExplainVerdict.SafeToForce"/> (uid-only rewrites, line endings, editor artifacts) needs
/// no confirmation at all — the same "--force is safe" case the console already prints without asking.
/// </remarks>
public sealed class ForceConfirmationPolicy : IWritePolicy {
    public string Name => "force_confirmation";

    public async ValueTask<WriteGuardVerdict> EvaluateAsync(WriteGuardContext context, CancellationToken cancellationToken) {
        if (!context.Force) {
            // Nothing would be discarded, because nothing is being forced.
            return WriteGuardVerdict.Allow();
        }

        var status = await context.InspectionWorkflow.StatusAsync(context.ProjectDirectory, explain: true, cancellationToken);

        var atRisk = status.Addons
            .Where(a => a.Kind == AddonStatusKind.Modified && !IsSafeToForce(a.Explanation))
            .ToList();

        if (atRisk.Count == 0) {
            return WriteGuardVerdict.Allow();
        }

        var confirmed = new HashSet<string>(context.ConfirmDiscard, StringComparer.Ordinal);
        var missing = atRisk.Where(a => !confirmed.Contains(a.Name)).ToList();

        if (missing.Count == 0) {
            return WriteGuardVerdict.Allow();
        }

        var names = string.Join(", ", missing.Select(a => $"'{a.Name}'"));

        return WriteGuardVerdict.Refuse(
            $"force would discard local modifications in {missing.Count} addon(s) not named in "
            + $"confirm_discard: {names}. Show the user what dotquay_status --explain reports for each "
            + "one, then retry with confirm_discard naming every one of them.",
            new ForceConfirmationDetailJson {
                Policy = Name,
                UnconfirmedAddons = missing
                    .Select(a => new StatusEntryJson(a.Name, JsonMapper.ToWire(a.Kind), JsonMapper.ToJson(a.Explanation)))
                    .ToList(),
            });
    }

    /// <summary>An addon with no explanation at all (status was queried without <c>explain</c>, which
    /// never happens here since <see cref="EvaluateAsync"/> always asks for one) is treated as unsafe
    /// — the absence of evidence is not evidence of safety.</summary>
    private static bool IsSafeToForce(AddonExplanation? explanation) => explanation?.Verdict == ExplainVerdict.SafeToForce;
}
