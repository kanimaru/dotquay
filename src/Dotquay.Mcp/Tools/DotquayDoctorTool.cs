using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay doctor</c>: sanity checks (git on PATH, dangling lockfile
/// entries, plugin.cfg drift, trust). See <c>Dotquay.Cli.Commands.DoctorCommand</c>.</summary>
public sealed class DotquayDoctorTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate) {
    public override string Name => "dotquay_doctor";

    public override string Description =>
        "Run sanity checks: git on PATH, dangling lockfile entries, plugin.cfg drift, trusted NuGet hosts "
        + "and adopted sources. Read-only.";

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
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken) {
        var workflow = McpInfrastructure.CreateWorkflow();
        var result = await workflow.DoctorAsync(projectDirectory, cancellationToken);
        var payload = JsonMapper.ToJson(result);

        // Issues are data, not a tool failure: IsError stays false and the caller reads
        // data.healthy — the same shape the CLI's --json path uses (DoctorCommand.cs).
        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<DoctorJson>.Success(Name, payload)));
    }
}
