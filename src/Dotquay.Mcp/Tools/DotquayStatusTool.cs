using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay status</c>: diffs disk against <c>dotquay.lock</c> for one
/// project (SPEC.md §7). See <c>Dotquay.Cli.Commands.StatusCommand</c> for the CLI's own copy of this
/// call pattern.</summary>
public sealed class DotquayStatusTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate)
{
    public override string Name => "dotquay_status";

    public override string Description =>
        "Diff disk against dotquay.lock for one Godot project: clean / patched as expected / modified / "
        + "missing / linked, per addon. Read-only.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "explain": {
              "type": "boolean",
              "description": "For each modified addon, compare it against its locked commit and report what differs and whether --force would discard anything. May fetch.",
              "default": false
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    protected override async ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken)
    {
        var explain = McpArgs.GetBool(arguments, "explain");
        var workflow = McpInfrastructure.CreateWorkflow(explain ? progress : null);

        var result = await workflow.StatusAsync(projectDirectory, explain, cancellationToken);
        var payload = JsonMapper.ToJson(result);

        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<StatusJson>.Success(Name, payload)));
    }
}
