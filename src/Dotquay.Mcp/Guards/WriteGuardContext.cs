using Dotquay.Core.Workflows;

namespace Dotquay.Mcp.Guards;

/// <summary>
/// Everything a write policy might need to decide, gathered once per tool call so a policy that
/// doesn't apply to this particular call (e.g. <see cref="ForceConfirmationPolicy"/> when the caller
/// never passed <c>force</c>) can say "allow" cheaply, and every policy reads the same shape.
/// </summary>
/// <remarks>
/// <see cref="InspectionWorkflow"/> is a real <see cref="DependencyWorkflow"/> a policy may call
/// read-only Core methods on (<c>StatusAsync</c>, <c>PreviewAddAsync</c>) to gather the facts its
/// verdict needs — never the workflow the write tool goes on to mutate with, which is why it is wired
/// with <see cref="Dotquay.Core.Abstractions.DenyAllSourceApproval"/>/<see
/// cref="Dotquay.Core.Abstractions.ShareGeneratorDecision"/> regardless of what the real call will
/// use: a pre-flight check must never itself adopt a feed or decide a generator's privacy.
/// </remarks>
public sealed record WriteGuardContext
{
    public required string ProjectDirectory { get; init; }

    public required DependencyWorkflow InspectionWorkflow { get; init; }

    /// <summary><c>force</c> as the caller passed it (§5.3). Only <see cref="ForceConfirmationPolicy"/>
    /// reads this.</summary>
    public bool Force { get; init; }

    /// <summary><c>confirm_discard</c> as the caller passed it. Only meaningful alongside
    /// <see cref="Force"/>.</summary>
    public IReadOnlyList<string> ConfirmDiscard { get; init; } = [];

    /// <summary>Present only for a tool that can preview a git add before running it
    /// (<c>dotquay_add</c>) — see <see cref="SourceApprovalPolicy"/>. Null for every other tool,
    /// including <c>dotquay_update</c>, which has no equivalent dry run (§5.1) and so is never
    /// pre-checked by this policy; it wires <see cref="Dotquay.Core.Abstractions.PreApprovedSourceApproval"/>
    /// straight into the real call and lets any refusal surface as an ordinary Core exception instead.</summary>
    public AddSourcePreviewRequest? AddPreview { get; init; }

    /// <summary><c>approved_source_urls</c> as the caller passed it.</summary>
    public IReadOnlyList<string> ApprovedSourceUrls { get; init; } = [];
}

/// <summary>What <see cref="SourceApprovalPolicy"/> needs to preview a git add before running it.</summary>
public sealed record AddSourcePreviewRequest(string GitUrl, AddonAddOptions Options);
