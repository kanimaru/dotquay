using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Tools;

/// <summary>
/// The catalogue of every tool this server exposes, and the two handlers
/// <see cref="Dotquay.Mcp.McpServerBuilder"/> wires to <c>tools/list</c> and <c>tools/call</c>.
/// </summary>
/// <remarks>
/// A plain list rather than a dictionary that also has to be kept in sync: <see cref="CallAsync"/>
/// does one linear scan over, at most, a few dozen tools per call — utterly immaterial next to the
/// git/NuGet work any real tool call does, and it means adding a tool is exactly one line
/// (<see cref="Dotquay.Mcp.McpServerBuilder"/> constructing this registry), never two.
/// </remarks>
public sealed class ToolRegistry(IReadOnlyList<IMcpTool> tools) {
    public ValueTask<ListToolsResult> ListAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ListToolsResult {
            Tools = tools
                .Select(tool => new Tool {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchema = tool.InputSchema,
                })
                .ToList(),
        });

    public ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken) {
        var name = context.Params?.Name;
        var tool = tools.FirstOrDefault(t => t.Name == name);

        if (tool is null) {
            return ValueTask.FromResult(McpToolResults.Failure(
                name ?? "(unknown)",
                Dotquay.Core.Json.JsonErrorKind.Unexpected,
                $"No such tool: '{name}'. Call tools/list for the tools this server exposes."));
        }

        return tool.CallAsync(context, cancellationToken);
    }
}
