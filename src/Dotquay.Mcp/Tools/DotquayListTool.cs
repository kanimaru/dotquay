using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay list</c>: installed addons and NuGet packages, straight from the
/// lockfile — instant, offline. See <c>Dotquay.Cli.Commands.ListCommand</c>.</summary>
public sealed class DotquayListTool(IProjectScope scope, ProjectGate gate, ClientRootsSync rootsSync) : McpToolBase(scope, gate, rootsSync)
{
    public override string Name => "dotquay_list";

    public override string Description =>
        "Show installed dependencies with their versions, straight from the lockfile. Read-only, offline.";

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

    protected override ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken)
    {
        var workflow = McpInfrastructure.CreateWorkflow();
        var addons = workflow.ListInstalled(projectDirectory);
        var dotnet = workflow.ListInstalledDotnet(projectDirectory);
        var payload = JsonMapper.ToListJson(addons, dotnet);

        return ValueTask.FromResult(McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<ListJson>.Success(Name, payload))));
    }
}
