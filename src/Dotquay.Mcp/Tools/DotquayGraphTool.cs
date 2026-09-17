using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay graph</c>: the dependency graph, who requires what, and where it
/// conflicts (SPEC.md §8). See <c>Dotquay.Cli.Commands.GraphCommand</c>.</summary>
public sealed class DotquayGraphTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate)
{
    public override string Name => "dotquay_graph";

    public override string Description =>
        "Show the dependency graph: who requires what, and where it conflicts. Read-only; --resolve mode "
        + "may fetch but never writes the manifest, lockfile, or addons/.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "resolve": {
              "type": "boolean",
              "description": "Re-walk the live dependency graph against real git refs instead of reading the lockfile. Slower and needs network, but the only mode that reports unresolved conflicts and the candidates that would settle them.",
              "default": false
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    protected override async ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken)
    {
        var resolve = McpArgs.GetBool(arguments, "resolve");
        var inspector = McpInfrastructure.CreateInspector();

        var report = resolve
            ? await inspector.InspectResolvedAsync(projectDirectory, cancellationToken)
            : inspector.Inspect(projectDirectory);

        var payload = JsonMapper.ToJson(report);
        var warnings = report.Warnings.Select(w => w.Message).ToList();

        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<GraphJson>.Success(Name, payload, warnings)));
    }
}
