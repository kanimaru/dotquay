using System.Text.Json;
using System.Text.Json.Serialization;
using Dotquay.Core.Json;

namespace Dotquay.Mcp.Json;

/// <summary>
/// Source-generated serialization metadata for every payload an MCP tool result can carry.
/// </summary>
/// <remarks>
/// <para>
/// Source generation for the same reason <see cref="DotquayJsonContext"/> exists: reflection-based
/// <c>JsonSerializer</c> is stripped by the trimmer under NativeAOT, dotquay's distribution target
/// (SPEC.md §12, OQ-6). Every closed generic a tool serializes must be listed below.
/// </para>
/// <para>
/// A separate context rather than reusing <see cref="DotquayJsonContext"/> directly, even though
/// every Phase 1 tool's payload type is already registered there: the Godot editor plugin depends on
/// <see cref="DotquayJsonContext"/> as a stable wire contract (SPEC.md §17), and Phase 2's write-guard
/// tools will need MCP-only request/response shapes that have no CLI <c>--json</c> equivalent (design
/// doc §5). Registering those in <see cref="DotquayJsonContext"/> later would mean editing the one
/// file the plugin's compatibility promise is about, for a change the plugin will never see. Owning
/// this context from Phase 1 means that boundary never has to be crossed.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(JsonEnvelope<StatusJson>))]
[JsonSerializable(typeof(JsonEnvelope<ListJson>))]
[JsonSerializable(typeof(JsonEnvelope<GraphJson>))]
[JsonSerializable(typeof(JsonEnvelope<OutdatedJson>))]
[JsonSerializable(typeof(JsonEnvelope<DoctorJson>))]
[JsonSerializable(typeof(JsonEnvelope<AddPreviewJson>))]
[JsonSerializable(typeof(JsonEnvelope<SourceListJson>))]
[JsonSerializable(typeof(JsonEnvelope<OperationJson>))]
[JsonSerializable(typeof(McpRefusalJson<ForceConfirmationDetailJson>))]
[JsonSerializable(typeof(McpRefusalJson<SourceApprovalDetailJson>))]
public sealed partial class DotquayMcpJsonContext : JsonSerializerContext
{
}

/// <summary>The write side of the contract — one entry point per payload type so no tool has to pick
/// the right <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/> itself (picking
/// the wrong one is exactly how an AOT serialization bug gets shipped).</summary>
public static class DotquayMcpJson
{
    public static string Write(JsonEnvelope<StatusJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayMcpJsonContext.Default.JsonEnvelopeStatusJson);

    public static string Write(JsonEnvelope<ListJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayMcpJsonContext.Default.JsonEnvelopeListJson);

    public static string Write(JsonEnvelope<GraphJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayMcpJsonContext.Default.JsonEnvelopeGraphJson);

    public static string Write(JsonEnvelope<OutdatedJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayMcpJsonContext.Default.JsonEnvelopeOutdatedJson);

    public static string Write(JsonEnvelope<DoctorJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayMcpJsonContext.Default.JsonEnvelopeDoctorJson);

    public static string Write(JsonEnvelope<AddPreviewJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayMcpJsonContext.Default.JsonEnvelopeAddPreviewJson);

    public static string Write(JsonEnvelope<SourceListJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayMcpJsonContext.Default.JsonEnvelopeSourceListJson);

    public static string Write(JsonEnvelope<OperationJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayMcpJsonContext.Default.JsonEnvelopeOperationJson);

    public static string Write(McpRefusalJson<ForceConfirmationDetailJson> refusal) =>
        JsonSerializer.Serialize(refusal, DotquayMcpJsonContext.Default.McpRefusalJsonForceConfirmationDetailJson);

    public static string Write(McpRefusalJson<SourceApprovalDetailJson> refusal) =>
        JsonSerializer.Serialize(refusal, DotquayMcpJsonContext.Default.McpRefusalJsonSourceApprovalDetailJson);
}
