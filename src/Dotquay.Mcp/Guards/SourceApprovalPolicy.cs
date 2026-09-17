using Dotquay.Core.Abstractions;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;
using Dotquay.Mcp.Json;

namespace Dotquay.Mcp.Guards;

/// <summary>
/// Feed adoption → <c>approved_source_urls</c> (docs/DESIGN-v0.18-MCP.md §5.1). <see
/// cref="ISourceApproval"/> is already a seam with a <see cref="PreApprovedSourceApproval"/>
/// implementation (v0.9); this policy is the MCP-side wiring around it, plus — for <c>dotquay_add</c>
/// only, the one write tool with a dry-run preview — a pre-flight check that refuses with the exact
/// list of feeds still needing approval instead of letting the real add run partway and fail on
/// whatever package happened to need the unapproved feed first.
/// </summary>
/// <remarks>
/// <c>dotquay_update</c> has no equivalent preview (§5.1), so it never calls
/// <see cref="EvaluateAsync"/> with an <see cref="AddSourcePreviewRequest"/> — it uses
/// <see cref="Resolve"/> alone to build its workflow's <see cref="ISourceApproval"/> and lets any
/// refusal surface the way it always has: an unapproved feed becomes a per-package hint that only
/// turns into a <see cref="Workflows.WorkflowException"/> if a package actually needed it and nothing
/// else could resolve it (<c>DependencyWorkflow.ApplyAdoption</c>'s own remarks explain why a refusal
/// is deliberately not fatal on its own). Nothing is added to Core either way.
/// </remarks>
public sealed class SourceApprovalPolicy : IWritePolicy
{
    public string Name => "source_approval";

    public async ValueTask<WriteGuardVerdict> EvaluateAsync(WriteGuardContext context, CancellationToken cancellationToken)
    {
        if (context.AddPreview is not { } request)
        {
            // No dry run available for this call (dotquay_update, or a tool that doesn't touch feeds
            // at all) — nothing to pre-check.
            return WriteGuardVerdict.Allow();
        }

        var preview = await context.InspectionWorkflow.PreviewAddAsync(
            context.ProjectDirectory, request.GitUrl, request.Options, context.ApprovedSourceUrls, cancellationToken);

        if (preview.SourcesNeedingApproval.Count == 0)
        {
            return WriteGuardVerdict.Allow();
        }

        var urls = string.Join(", ", preview.SourcesNeedingApproval.Select(s => s.Url));

        return WriteGuardVerdict.Refuse(
            $"'{request.GitUrl}' declares {preview.SourcesNeedingApproval.Count} NuGet feed(s) this project "
            + $"has not agreed to trust: {urls}. Show the user each feed and what it would own, then retry "
            + "with approved_source_urls naming every one the user accepts.",
            new SourceApprovalDetailJson
            {
                Policy = Name,
                UnapprovedSources = preview.SourcesNeedingApproval.Select(ToJson).ToList(),
            });
    }

    /// <summary>Builds the <see cref="ISourceApproval"/> a write tool wires into its real workflow —
    /// used whether or not <see cref="EvaluateAsync"/> ran a pre-flight check.</summary>
    public static ISourceApproval Resolve(IReadOnlyList<string> approvedSourceUrls) =>
        PreApprovedSourceApproval.For(approvedSourceUrls);

    /// <summary>
    /// Maps a <see cref="PreviewSource"/> to its wire shape by hand: <c>JsonMapper.ToJson(PreviewSource)</c>
    /// is private (it's an implementation detail of mapping a whole <see cref="AddPreview"/>, not a
    /// public API), so a refusal's <c>detail.unapproved_sources</c> — which needs exactly one entry,
    /// not a whole preview — builds <see cref="PreviewSourceJson"/> directly from the same fields.
    /// </summary>
    private static PreviewSourceJson ToJson(PreviewSource source) => new()
    {
        Name = source.Name,
        Url = source.Url,
        DeclaredByAddon = source.DeclaredByAddon,
        IsTrustedHost = source.IsTrustedHost,
        Packages = source.Packages,
        PackageIds = source.PackageIds,
    };
}
