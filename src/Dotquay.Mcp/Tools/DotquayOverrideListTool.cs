using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Core.Resolve;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of a bare <c>dotquay override</c> (no name): lists the project's current
/// <c>[overrides.*]</c> entries, dotquay's unconditional escape hatch out of a dependency conflict
/// (SPEC.md §7.1/§8). See <c>Dotquay.Cli.Commands.OverrideCommand</c>.</summary>
/// <remarks>
/// Reuses <see cref="OperationJson"/>/<see cref="ChangeJson"/> exactly as the CLI's own listing does
/// (<c>OverrideCommand.ListOverrides</c>): an override entry already fits that shape (name, kind,
/// from/to), so a second payload type would say nothing this one doesn't.
/// </remarks>
public sealed class DotquayOverrideListTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate)
{
    public override string Name => "dotquay_override_list";

    public override string Description => "List the project's current dependency overrides ([overrides.*]). Read-only.";

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
        var entries = McpInfrastructure.CreateOverrides().List(projectDirectory);

        var payload = new OperationJson
        {
            Changes = entries
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new ChangeJson
                {
                    Name = kv.Key,
                    Kind = JsonMapper.ChangeKind.Addon,
                    Action = JsonMapper.ChangeAction.Unchanged,
                    From = JsonMapper.ToWire(kv.Value.SourceKind),
                    To = AddonRequirement.ConstraintText(kv.Value),
                })
                .ToList(),
        };

        return ValueTask.FromResult(McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, payload))));
    }
}
