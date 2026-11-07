using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dotquay.Core.Json;

/// <summary>
/// Source-generated serialization metadata for every <c>--json</c> payload. Source generation is
/// not an optimisation here: reflection-based <c>JsonSerializer</c> is stripped by the trimmer
/// under NativeAOT, which is dotquay's distribution target (SPEC.md §12, OQ-6) — the same class of
/// failure that already forced Spectre.Console.Cli and NuGet.Protocol out of this project
/// (docs/SPRINT-01.md). Every closed generic that gets serialized must be listed below; a missing
/// one fails at runtime in the published binary while building and running fine under JIT, so
/// <c>graph --json</c> is exercised against a real AOT publish, not just <c>dotnet run</c>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(JsonEnvelope<GraphJson>))]
[JsonSerializable(typeof(JsonEnvelope<ListJson>))]
[JsonSerializable(typeof(JsonEnvelope<StatusJson>))]
[JsonSerializable(typeof(JsonEnvelope<OutdatedJson>))]
[JsonSerializable(typeof(JsonEnvelope<DoctorJson>))]
[JsonSerializable(typeof(JsonEnvelope<OperationJson>))]
[JsonSerializable(typeof(JsonEnvelope<LoginJson>))]
[JsonSerializable(typeof(JsonEnvelope<AddPreviewJson>))]
[JsonSerializable(typeof(JsonEnvelope<SourceListJson>))]
[JsonSerializable(typeof(JsonEnvelope<SelfCheckJson>))]
[JsonSerializable(typeof(JsonEnvelope<SelfUpdateJson>))]
public sealed partial class DotquayJsonContext : JsonSerializerContext {
}

/// <summary>The write side of the contract — one entry point per payload type so no caller has to
/// pick the right <see cref="JsonTypeInfo"/> itself (picking the wrong one is exactly how an AOT
/// serialization bug gets shipped).</summary>
public static class DotquayJson {
    public static string Write(JsonEnvelope<GraphJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayJsonContext.Default.JsonEnvelopeGraphJson);

    public static string Write(JsonEnvelope<ListJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayJsonContext.Default.JsonEnvelopeListJson);

    public static string Write(JsonEnvelope<StatusJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayJsonContext.Default.JsonEnvelopeStatusJson);

    public static string Write(JsonEnvelope<OutdatedJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayJsonContext.Default.JsonEnvelopeOutdatedJson);

    public static string Write(JsonEnvelope<DoctorJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayJsonContext.Default.JsonEnvelopeDoctorJson);

    public static string Write(JsonEnvelope<OperationJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayJsonContext.Default.JsonEnvelopeOperationJson);

    public static string Write(JsonEnvelope<LoginJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayJsonContext.Default.JsonEnvelopeLoginJson);

    public static string Write(JsonEnvelope<AddPreviewJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayJsonContext.Default.JsonEnvelopeAddPreviewJson);

    public static string Write(JsonEnvelope<SourceListJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayJsonContext.Default.JsonEnvelopeSourceListJson);

    public static string Write(JsonEnvelope<SelfCheckJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayJsonContext.Default.JsonEnvelopeSelfCheckJson);

    public static string Write(JsonEnvelope<SelfUpdateJson> envelope) =>
        JsonSerializer.Serialize(envelope, DotquayJsonContext.Default.JsonEnvelopeSelfUpdateJson);
}
