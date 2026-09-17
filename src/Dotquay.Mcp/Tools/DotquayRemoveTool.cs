using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay remove &lt;name&gt;</c>: drops a dependency from the manifest,
/// lockfile, and disk. See <c>Dotquay.Cli.Commands.RemoveCommand</c>.</summary>
/// <remarks>
/// Destructive and unguarded by design (Â§4.2): it deletes <c>addons/&lt;folder&gt;</c> through
/// <c>InstallPaths</c> and the sync engine like every other caller, so it never follows a directory
/// link, but nothing stops it from deleting an addon with uncaptured local edits — that is what
/// "explained destruction" means here (Â§4.2's own reasoning): the agent can, and the
/// <c>dotquay_diagnose_modified</c> prompt (Â§7, Phase 3) tells it to, run <c>dotquay_status</c> first.
/// </remarks>
public sealed class DotquayRemoveTool(IProjectScope scope, ProjectGate gate, ClientRootsSync rootsSync) : McpToolBase(scope, gate, rootsSync)
{
    public override string Name => "dotquay_remove";

    public override string Description =>
        "Remove an addon or dotnet dependency from the manifest, lockfile, and disk. Destructive — " +
        "consider calling dotquay_status first if the addon might have unsaved local edits.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "name": { "type": "string", "description": "The addon or dotnet dependency name to remove." }
          },
          "required": ["name"],
          "additionalProperties": false
        }
        """).RootElement;

    protected override async ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken)
    {
        var name = McpArgs.GetString(arguments, "name");

        if (string.IsNullOrWhiteSpace(name))
        {
            return McpToolResults.Failure(Name, JsonErrorKind.Workflow, "'name' is required: the dependency to remove.");
        }

        var workflow = McpInfrastructure.CreateWorkflow();
        await workflow.RemoveAsync(projectDirectory, name, cancellationToken);

        var payload = new OperationJson
        {
            Changes =
            [
                new ChangeJson
                {
                    Name = name,
                    // RemoveAsync accepts either an addon or a dotnet name and doesn't report which
                    // it matched — see RemoveCommand.cs's identical comment for why the wire value
                    // stays "addon" rather than a third "unknown" kind.
                    Kind = JsonMapper.ChangeKind.Addon,
                    Action = JsonMapper.ChangeAction.Removed,
                },
            ],
            Messages = [$"Removed '{name}'."],
        };

        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, payload)));
    }
}
