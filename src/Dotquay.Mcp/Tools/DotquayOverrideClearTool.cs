using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Core.Workflows;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay override &lt;name&gt; --clear</c>: removes an override and hands
/// the name back to normal resolution (SPEC.md §7.1/§8). See <c>Dotquay.Cli.Commands.OverrideCommand</c>.
/// </summary>
/// <remarks>A pure manifest edit, same as <see cref="DotquayOverrideSetTool"/> — no follow-on resync.
/// Clearing an override may resurface the conflict it was hiding; that's the point of an override
/// being a decision the user can take back.</remarks>
public sealed class DotquayOverrideClearTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate)
{
    public override string Name => "dotquay_override_clear";

    public override string Description =>
        "Remove an addon's [overrides.<name>] entry, handing it back to normal conflict resolution. " +
        "Writes the manifest only; call dotquay_update to apply the change.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "name": { "type": "string", "description": "The addon whose override to remove." }
          },
          "required": ["name"],
          "additionalProperties": false
        }
        """).RootElement;

    protected override ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken)
    {
        var name = McpArgs.GetString(arguments, "name");

        if (string.IsNullOrWhiteSpace(name))
        {
            return ValueTask.FromResult(McpToolResults.Failure(Name, JsonErrorKind.Workflow, "'name' is required: the override to remove."));
        }

        var overrides = McpInfrastructure.CreateOverrides();
        var result = overrides.Clear(projectDirectory, name);

        var message = result.Change == OverrideChange.Removed
            ? $"Removed [overrides.{result.Name}]. Call dotquay_update to apply the change."
            : $"[overrides.{result.Name}] didn't exist — nothing to do.";

        var payload = new OperationJson
        {
            Changes =
            [
                new ChangeJson
                {
                    Name = result.Name,
                    Kind = JsonMapper.ChangeKind.Addon,
                    Action = result.Change == OverrideChange.Removed ? JsonMapper.ChangeAction.Removed : JsonMapper.ChangeAction.Unchanged,
                },
            ],
            Messages = [message],
        };

        return ValueTask.FromResult(McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, payload))));
    }
}
