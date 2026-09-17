using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Core.Workflows;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay patch &lt;name&gt;</c>: captures hand-edits to an installed
/// addon as a patch file (SPEC.md §10). See <c>Dotquay.Cli.Commands.PatchCommand</c>.</summary>
/// <remarks>Not destructive and unguarded: it only ever writes a patch file describing what's already
/// on disk, and re-applying it on the next install/update is automatic (see the project's own
/// "Patches" notes on why that's easy to mistake for broken).</remarks>
public sealed class DotquayPatchCaptureTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate)
{
    public override string Name => "dotquay_patch_capture";

    public override string Description =>
        "Capture hand-edits to an installed addon as a patch file, so they survive the next install/update.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "name": { "type": "string", "description": "The installed addon to diff against its pristine cache copy." }
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
            return McpToolResults.Failure(Name, JsonErrorKind.Workflow, "'name' is required: the installed addon to capture a patch from.");
        }

        var workflow = McpInfrastructure.CreateWorkflow();
        var result = await workflow.PatchAsync(projectDirectory, name, cancellationToken);

        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, ToJson(result))));
    }

    internal static OperationJson ToJson(PatchCaptureResult result) => new()
    {
        Changes =
        [
            new ChangeJson
            {
                Name = result.Name,
                Kind = JsonMapper.ChangeKind.Addon,
                Action = result.Captured ? JsonMapper.ChangeAction.Updated : JsonMapper.ChangeAction.Unchanged,
                To = result.Captured ? result.PatchFilePath : null,
            },
        ],
        Messages =
        [
            result.Captured
                ? $"Captured '{result.Name}' -> {result.PatchFilePath}"
                : $"'{result.Name}' matches its pristine base — nothing to capture.",
        ],
    };
}
