using Dotquay.Core.Sync;

namespace Dotquay.Core.Abstractions;

/// <summary>
/// Decides whether to accept dotquay's guess at where an addon lives inside its own repository, when
/// the guessed <c>addons/&lt;name&gt;</c> subtree isn't there
/// (docs/DESIGN-v0.20-SUBTREE-GUESS.md §4).
/// </summary>
/// <remarks>
/// A seam rather than a console prompt inside the workflow, for the same reason
/// <see cref="ISourceApproval"/> and <see cref="IGeneratorAssetDecision"/> are seams:
/// <c>Dotquay.Core</c> holds no console code (SPEC.md §12), the Godot editor plugin drives dotquay
/// through captured stdout and has no way to answer a question, and a workflow that blocks on
/// <c>Console.ReadLine</c> is untestable. The CLI implements it as a prompt; <c>--json</c>, CI and
/// the plugin use <see cref="DeclineSubtreeChoice"/>.
/// <para>
/// There is deliberately no pre-approved implementation mirroring
/// <see cref="PreApprovedSourceApproval"/>. A caller that showed the suggestion in its own UI and got
/// a yes replays it as <c>--path</c>/<c>--install-as</c>, which is what the user actually agreed to —
/// and an explicitly stated path is never probed or second-guessed, so the question simply doesn't
/// arise on that run. One replay channel, and the manifest ends up recording the same two values
/// either way.
/// </para>
/// </remarks>
public interface ISubtreeChoice
{
    /// <summary>True to install from <see cref="SubtreeChoiceRequest.Suggestion"/> and write it into
    /// the manifest. Never called when the caller passed <c>--path</c>, when the guessed subtree
    /// exists, or for a transitive addon — whose path is someone else's manifest to fix.</summary>
    bool Accept(SubtreeChoiceRequest request);
}

/// <summary>What the user is being asked to agree to: which addon, which repository, and which
/// folder inside it dotquay believes is the addon.</summary>
/// <param name="AddonName">The manifest key <c>add</c> derived from the URL. Unchanged by an accepted
/// suggestion — this corrects layout, never identity.</param>
/// <param name="Git">The repository, so the question names something the user can go and look at.</param>
/// <param name="Finding">Everything the repository offered, including the candidates that were not
/// suggested.</param>
/// <param name="Suggestion">The candidate being proposed. Always
/// <see cref="SubtreeFinding.Suggestion"/>, hoisted out so an implementation doesn't have to
/// re-establish that it is non-null.</param>
public sealed record SubtreeChoiceRequest(
    string AddonName, string Git, SubtreeFinding Finding, SubtreeCandidate Suggestion);

/// <summary>
/// Refuses every guess.
/// </summary>
/// <remarks>
/// The default, and deliberately so. Anywhere that can't ask a human — a CI run, <c>--json</c>, the
/// editor plugin — must not quietly install an addon from a folder nobody named; it should fail and
/// say which flags to pass. Refusal is not an error in itself: the caller decides what an unanswered
/// guess means, and <c>add --preview</c> reports it rather than failing.
/// </remarks>
public sealed class DeclineSubtreeChoice : ISubtreeChoice
{
    public static readonly DeclineSubtreeChoice Instance = new();

    public bool Accept(SubtreeChoiceRequest request) => false;
}
