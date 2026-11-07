using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Guards;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay update [name]</c>: re-resolves one dependency, or everything the
/// manifest declares, fresh (SPEC.md §6). See <c>Dotquay.Cli.Commands.UpdateCommand</c>.</summary>
/// <remarks>
/// <see cref="Guards.SourceApprovalPolicy"/> has no dry run for <c>update</c> (§5.1 — there is no
/// <c>PreviewUpdateAsync</c>), so <c>approved_source_urls</c> is wired straight into the real
/// workflow's <see cref="Dotquay.Core.Abstractions.ISourceApproval"/> and never pre-checked; an
/// unadopted feed a package actually needed surfaces the way it always has, as an ordinary Core
/// exception naming the addon and feed. <see cref="Guards.ForceConfirmationPolicy"/> is pre-checked,
/// same as <see cref="DotquayInstallTool"/>.
/// </remarks>
public sealed class DotquayUpdateTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate) {
    public override string Name => "dotquay_update";

    public override string Description =>
        "Re-resolve one dependency (name) or everything the manifest declares, fresh, and reinstall it. " +
        "force: true overwrites addons with unrecorded local edits; may refuse with needs_confirmation " +
        "unless confirm_discard names every one that would lose real content.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "name": {
              "type": "string",
              "description": "The addon or dotnet package to update. Omit to update everything the manifest declares."
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
            },
            "approved_source_urls": {
              "type": "array",
              "items": { "type": "string" },
              "description": "NuGet feed URLs an addon in the graph declares, that the user agrees to trust for this update."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    protected override async ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken) {
        var name = McpArgs.GetString(arguments, "name");
        var force = McpArgs.GetBool(arguments, "force");
        var confirmDiscard = McpArgs.GetStringArray(arguments, "confirm_discard");
        var approvedSourceUrls = McpArgs.GetStringArray(arguments, "approved_source_urls");

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

        var workflow = McpInfrastructure.CreateWorkflow(progress, SourceApprovalPolicy.Resolve(approvedSourceUrls), generatorAssetDecision: null);
        var result = await workflow.UpdateAsync(projectDirectory, name, force, cancellationToken);
        var payload = JsonMapper.ToJson(result);

        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, payload, result.Warnings)));
    }
}
