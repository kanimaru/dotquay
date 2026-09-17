using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay outdated</c>: checks for newer versions without cloning anything.
/// See <c>Dotquay.Cli.Commands.OutdatedCommand</c>.</summary>
public sealed class DotquayOutdatedTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate)
{
    public override string Name => "dotquay_outdated";

    public override string Description =>
        "Check for newer versions of every installed addon and NuGet package, without cloning anything. "
        + "Read-only, but hits the network (git ls-remote / NuGet) for every entry.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    protected override async ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken)
    {
        var workflow = McpInfrastructure.CreateWorkflow(progress);
        var result = await workflow.OutdatedAsync(projectDirectory, cancellationToken);
        var payload = JsonMapper.ToJson(result);

        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OutdatedJson>.Success(Name, payload)));
    }
}
