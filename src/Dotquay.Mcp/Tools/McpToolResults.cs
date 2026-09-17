using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Mcp.Json;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>
/// Turns a serialized envelope into the two representations MCP wants back from a tool call: the
/// text block every client can read, and — for a client that reads structured content instead of
/// re-parsing text — the already-parsed document.
/// </summary>
/// <remarks>
/// Parses the JSON it just wrote with <see cref="JsonDocument"/> rather than
/// <c>JsonNode</c>/<c>JsonSerializer.Deserialize&lt;JsonElement&gt;</c>: the former is what
/// <see cref="CallToolResult.StructuredContent"/> is typed as, and the latter two either don't match
/// that type or trigger the IL2026/IL3050 trim warnings <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/>
/// doesn't (confirmed during the Phase 0 spike — docs/DESIGN-v0.18-MCP.md §3.1).
/// </remarks>
internal static class McpToolResults
{
    public static CallToolResult FromJson(string json, bool isError = false) => new()
    {
        Content = [new TextContentBlock { Text = json }],
        StructuredContent = JsonDocument.Parse(json).RootElement,
        IsError = isError,
    };

    /// <summary>The one shape every tool failure takes, mirroring
    /// <c>Dotquay.Cli.Commands.JsonOutput.Fail</c>: always <see cref="OperationJson"/>, regardless of
    /// which payload the tool would have returned on success, so a client parses one failure shape for
    /// every tool rather than one per success schema.</summary>
    public static CallToolResult Failure(string toolName, string kind, string message) =>
        FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Failure(toolName, new JsonError(kind, message))), isError: true);
}
