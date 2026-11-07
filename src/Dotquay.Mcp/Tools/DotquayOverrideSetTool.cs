using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Manifest;
using Dotquay.Core.Progress;
using Dotquay.Core.Resolve;
using Dotquay.Core.Workflows;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay override &lt;name&gt;</c>: forces one addon to a specific
/// version, branch, or commit — the conflict escape hatch (SPEC.md §7.1/§8). See
/// <c>Dotquay.Cli.Commands.OverrideCommand</c>.</summary>
/// <remarks>
/// A pure manifest edit and nothing else — per the table (§4.2) this tool's Core call is
/// <c>OverrideWorkflow.Set</c> alone, not the CLI's follow-on resync (<c>DependencyWorkflow.ResyncAsync</c>).
/// <c>OverrideWorkflow</c>'s own remarks explain why writing and applying an override are kept apart:
/// a caller can write one, see a later <c>dotquay_update</c> fail, and clear it again without an
/// install having half-happened in between. An agent applies the override by calling
/// <c>dotquay_update</c> (or <c>dotquay_install</c>) itself, which is also where the write guard's
/// force/confirm_discard check belongs — a plain manifest edit never discards anything on its own.
/// </remarks>
public sealed class DotquayOverrideSetTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate)
{
    public override string Name => "dotquay_override_set";

    public override string Description =>
        "Force one addon to a version, branch, or commit by writing [overrides.<name>] — settles a " +
        "dependency conflict. Writes the manifest only; call dotquay_update to apply it.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "name": { "type": "string", "description": "The addon to force." },
            "git": {
              "type": "string",
              "description": "Repository to pin to. Defaults to whatever the manifest or lockfile already records for this name."
            },
            "version": { "type": "string", "description": "Force a version constraint, e.g. \"~1.4\"." },
            "branch": { "type": "string", "description": "Force a branch; the lockfile still pins the resolved SHA." },
            "rev": { "type": "string", "description": "Force an exact commit." },
            "path": { "type": "string", "description": "Subtree override, if it isn't addons/<name>." }
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
            return ValueTask.FromResult(McpToolResults.Failure(Name, JsonErrorKind.Workflow, "'name' is required: the addon to override."));
        }

        var version = McpArgs.GetString(arguments, "version");
        var branch = McpArgs.GetString(arguments, "branch");
        var rev = McpArgs.GetString(arguments, "rev");

        var named = new[] { version, branch, rev }.Count(v => v is not null);

        if (named != 1)
        {
            return ValueTask.FromResult(McpToolResults.Failure(
                Name, JsonErrorKind.Workflow,
                $"'{name}': pass exactly one of version, branch, or rev."));
        }

        var (kind, value) = version is not null ? (AddonSourceKind.Version, version)
            : branch is not null ? (AddonSourceKind.Branch, branch)
            : (AddonSourceKind.Rev, rev!);

        var overrides = McpInfrastructure.CreateOverrides();
        var result = overrides.Set(projectDirectory, name, kind, value, McpArgs.GetString(arguments, "git"), McpArgs.GetString(arguments, "path"));

        var payload = new OperationJson
        {
            Changes =
            [
                new ChangeJson
                {
                    Name = result.Name,
                    Kind = JsonMapper.ChangeKind.Addon,
                    Action = result.Change switch
                    {
                        OverrideChange.Added => JsonMapper.ChangeAction.Added,
                        OverrideChange.Replaced => JsonMapper.ChangeAction.Updated,
                        OverrideChange.Removed => JsonMapper.ChangeAction.Removed,
                        _ => JsonMapper.ChangeAction.Unchanged,
                    },
                    To = result.Spec is null ? null : AddonRequirement.ConstraintText(result.Spec),
                },
            ],
            Messages = [Describe(result)],
        };

        return ValueTask.FromResult(McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, payload))));
    }

    private static string Describe(OverrideResult result) => result.Change switch
    {
        OverrideChange.Added => $"Added [overrides.{result.Name}] -> {AddonRequirement.ConstraintText(result.Spec!)}. Call dotquay_update to apply it.",
        OverrideChange.Replaced => $"Changed [overrides.{result.Name}] -> {AddonRequirement.ConstraintText(result.Spec!)}. Call dotquay_update to apply it.",
        _ => $"[overrides.{result.Name}] already says that — nothing to do.",
    };
}
