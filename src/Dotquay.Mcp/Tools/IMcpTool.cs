using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Tools;

/// <summary>
/// One MCP tool: its advertised shape (<see cref="Name"/>, <see cref="Description"/>,
/// <see cref="InputSchema"/>) plus the handler that runs a call.
/// </summary>
/// <remarks>
/// An interface with concrete implementing classes, not <c>[McpServerTool]</c> attributes on methods
/// discovered via <c>WithToolsFromAssembly</c>: the reflection-based attribute API is exactly what
/// Phase 0 rejected in favour of the SDK's low-level handler API, precisely to keep tool registration
/// free of reflection under NativeAOT (docs/DESIGN-v0.18-MCP.md §3.1). Every tool here is a plain
/// object <see cref="Tools.ToolRegistry"/> can enumerate and dispatch to by name — no attribute
/// scanning, ever.
/// </remarks>
public interface IMcpTool
{
    /// <summary>The wire name a client calls, e.g. <c>"dotquay_status"</c>.</summary>
    string Name { get; }

    /// <summary>Shown to the model deciding whether to call this tool — worth being precise and
    /// naming the underlying dotquay command a human would run instead.</summary>
    string Description { get; }

    /// <summary>A JSON Schema object literal (never built via reflection — see the type's own
    /// remarks) describing the tool's arguments.</summary>
    System.Text.Json.JsonElement InputSchema { get; }

    ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken);
}
