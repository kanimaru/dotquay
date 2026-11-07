using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Guards;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay install</c>: replays the lockfile exactly — never re-resolves
/// (SPEC.md §5). See <c>Dotquay.Cli.Commands.InstallCommand</c>.</summary>
/// <remarks>
/// Never adopts a NuGet feed even with <see cref="Dotquay.Core.Abstractions.PreApprovedSourceApproval"/>
/// available — <c>Dotquay.Cli.Commands.WorkflowFactory.CreateWithProgress</c>'s own remarks explain why
/// on principle: a command whose whole contract is "replay the lockfile" should never be one refactor
/// away from acquiring a feed. Guarded by <see cref="ForceConfirmationPolicy"/> only.
/// </remarks>
public sealed class DotquayInstallTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate) {
    public override string Name => "dotquay_install";

    public override string Description =>
        "Install exactly what dotquay.lock pins — never re-resolves. force: true overwrites addons with " +
        "unrecorded local edits; may refuse with needs_confirmation unless confirm_discard names every " +
        "one that would lose real content.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "force": {
              "type": "boolean",
              "description": "Overwrite addons with local modifications that were never captured as a patch.",
              "default": false
            },
            "confirm_discard": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Addon names the user has agreed to discard local modifications from, as reported by a prior needs_confirmation refusal or dotquay_status --explain."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    protected override async ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken) {
        var force = McpArgs.GetBool(arguments, "force");
        var confirmDiscard = McpArgs.GetStringArray(arguments, "confirm_discard");

        var guardContext = new WriteGuardContext {
            ProjectDirectory = projectDirectory,
            InspectionWorkflow = McpInfrastructure.CreateWorkflow(),
            Force = force,
            ConfirmDiscard = confirmDiscard,
        };

        var verdict = await WriteGuard.Default.EvaluateAsync(guardContext, cancellationToken);

        if (verdict is WriteGuardRefusal refusal) {
            return McpToolResults.FromRefusal(Name, refusal);
        }

        var workflow = McpInfrastructure.CreateWorkflow(progress);
        var result = await workflow.InstallAsync(projectDirectory, force, cancellationToken);
        var payload = JsonMapper.ToJson(result);

        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, payload)));
    }
}
