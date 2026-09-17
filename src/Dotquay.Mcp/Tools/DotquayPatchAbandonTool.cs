using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay patch &lt;name&gt; --abandon</c>: drops the addon's patch and
/// takes upstream as-is (SPEC.md §10). See <c>Dotquay.Cli.Commands.PatchCommand</c>.</summary>
/// <remarks>
/// Destructive (§4.2: "drops the patch") and unguarded by the write-guard chain — <c>force: true</c>
/// is not a caller-supplied argument here, it is <c>AbandonPatchAsync</c>'s own unconditional
/// behaviour (matching <c>PatchCommand.cs</c>'s call exactly), because abandoning a patch is already
/// an explicit, single-addon decision an agent cannot reach by accident the way a bulk
/// <c>update --force</c> could.
/// </remarks>
public sealed class DotquayPatchAbandonTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate)
{
    public override string Name => "dotquay_patch_abandon";

    public override string Description =>
        "Drop an addon's captured patch and reinstall it from upstream as-is. Destructive: the captured edits are lost.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "name": { "type": "string", "description": "The addon whose patch to drop." }
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
            return McpToolResults.Failure(Name, JsonErrorKind.Workflow, "'name' is required: the addon whose patch to abandon.");
        }

        var workflow = McpInfrastructure.CreateWorkflow();
        var result = await workflow.AbandonPatchAsync(projectDirectory, name, force: true, cancellationToken);

        var payload = new OperationJson
        {
            Changes =
            [
                new ChangeJson
                {
                    Name = result.Name,
                    Kind = JsonMapper.ChangeKind.Addon,
                    Action = JsonMapper.ChangeAction.Removed,
                    To = null,
                },
            ],
            Messages = [$"Dropped '{result.Name}'s patch and reinstalled it from upstream."],
        };

        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, payload)));
    }
}
