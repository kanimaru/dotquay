using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay patch &lt;name&gt; --resolve</c>: finishes a conflicted patch
/// re-apply and recaptures against the new upstream commit (SPEC.md Â§10). See
/// <c>Dotquay.Cli.Commands.PatchCommand</c>.</summary>
/// <remarks>Not destructive and unguarded: it only proceeds once every conflict marker in staging is
/// gone (<c>DependencyWorkflow.ResolvePatchAsync</c> refuses otherwise), so there is nothing left for
/// a human to have been asked about — the conflict itself already stopped the update that led here.
/// </remarks>
public sealed class DotquayPatchResolveTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate)
{
    public override string Name => "dotquay_patch_resolve";

    public override string Description =>
        "Finish a conflicted patch re-apply: install the resolved files from staging, advance the " +
        "lockfile, and recapture the patch against the new upstream commit.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "name": { "type": "string", "description": "The addon whose conflicted update is waiting to be resolved." }
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
            return McpToolResults.Failure(Name, JsonErrorKind.Workflow, "'name' is required: the addon whose conflict to resolve.");
        }

        var workflow = McpInfrastructure.CreateWorkflow();
        var result = await workflow.ResolvePatchAsync(projectDirectory, name, cancellationToken);

        var payload = DotquayPatchCaptureTool.ToJson(result) with
        {
            Messages =
            [
                result.Captured
                    ? $"Resolved '{result.Name}' and recaptured its patch -> {result.PatchFilePath}"
                    : $"Resolved '{result.Name}'. The result matches upstream exactly, so there is no patch left to keep.",
            ],
        };

        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, payload)));
    }
}
