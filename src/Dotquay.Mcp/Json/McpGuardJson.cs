using Dotquay.Core.Json;

namespace Dotquay.Mcp.Json;

// Detail payloads for a needs_confirmation refusal (docs/DESIGN-v0.18-MCP.md §5.4). Each write
// policy that can refuse (Dotquay.Mcp.Guards) has its own shape here — reusing StatusEntryJson and
// PreviewSourceJson from Dotquay.Core.Json rather than inventing parallel ones, since a refusal is
// showing the agent exactly the same facts dotquay_status/dotquay_add_preview would have.

/// <summary>Detail for a <c>Dotquay.Mcp.Guards.ForceConfirmationPolicy</c> refusal: which addons
/// <c>force</c> would discard real content from, and why.</summary>
public sealed record ForceConfirmationDetailJson
{
    public required string Policy { get; init; }

    public IReadOnlyList<StatusEntryJson> UnconfirmedAddons { get; init; } = [];
}

/// <summary>Detail for a <c>Dotquay.Mcp.Guards.SourceApprovalPolicy</c> refusal: which NuGet feeds
/// the add would need to trust that weren't in <c>approved_source_urls</c>.</summary>
public sealed record SourceApprovalDetailJson
{
    public required string Policy { get; init; }

    public IReadOnlyList<PreviewSourceJson> UnapprovedSources { get; init; } = [];
}

/// <summary>
/// The structured failure a write-guard refusal returns (§5.4): the same envelope fields every
/// <c>--json</c>/tool failure already uses (<see cref="JsonEnvelope{T}"/>), plus <c>detail</c> — a
/// policy-specific object naming exactly what the agent must show the user and pass back to retry.
/// </summary>
/// <remarks>
/// A generic type closed over each concrete detail shape, the same trick <see cref="JsonEnvelope{T}"/>
/// itself uses (and <see cref="DotquayMcpJsonContext"/> already relies on for every payload type) —
/// not a shared <c>JsonError</c> extension, because <see cref="JsonError"/> is part of the Godot
/// plugin's wire contract (SPEC.md §17) and this shape exists only at the MCP boundary.
/// </remarks>
public sealed record McpRefusalJson<TDetail>
    where TDetail : class
{
    public int Schema { get; init; } = JsonEnvelope<OperationJson>.CurrentSchema;

    public string Tool { get; init; } = JsonEnvelope<OperationJson>.ToolName;

    public required string Version { get; init; }

    public required string Command { get; init; }

    public bool Ok { get; init; }

    public required JsonError Error { get; init; }

    public required TDetail Detail { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
