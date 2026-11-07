using System.Text.Json;
using Dotquay.Core.Init;
using Dotquay.Core.IO;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay init</c>: scaffolds a project or package manifest. See
/// <c>Dotquay.Cli.Commands.InitCommand</c>.</summary>
/// <remarks>
/// No write guard applies (§4.2's table lists none for <c>dotquay_init</c>): scaffolding a manifest
/// touches nothing that was ever going to prompt a human, and refuses to overwrite an existing
/// manifest on its own (reported as <c>unchanged</c>, not an error — the same "the state you asked for
/// already exists" reasoning <c>InitCommand</c> uses).
/// </remarks>
public sealed class DotquayInitTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate) {
    public override string Name => "dotquay_init";

    public override string Description =>
        "Scaffold a dotquay.toml: a project manifest by default, or a package manifest (for an addon) "
        + "with package: true. Refuses to overwrite an existing manifest — reports unchanged instead.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the directory to initialize. Defaults to the server's first --project root."
            },
            "package": {
              "type": "boolean",
              "description": "Scaffold a package manifest (with a [package] header) for an addon, instead of a project manifest.",
              "default": false
            },
            "name": {
              "type": "string",
              "description": "Package name. Only used with package: true. Defaults to the directory name."
            },
            "nuget_source": {
              "type": "string",
              "description": "V3 service-index URL of a private NuGet feed. Only used without package: true. Omit to use the public nuget.org."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    protected override ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken) {
        var isPackage = McpArgs.GetBool(arguments, "package");
        var initService = new InitService(new SystemFileSystem());

        var result = isPackage
            ? initService.InitPackage(projectDirectory, McpArgs.GetString(arguments, "name") ?? new DirectoryInfo(projectDirectory).Name)
            : initService.InitProject(projectDirectory, McpArgs.GetString(arguments, "nuget_source"));

        var kind = isPackage ? "package" : "project";

        var message = result.WasCreated
            ? $"Wrote {kind} manifest to {result.ManifestPath}"
            : $"{result.ManifestPath} already exists — not overwriting.";

        var payload = new OperationJson {
            Changes =
            [
                new ChangeJson
                {
                    Name = kind,
                    Kind = JsonMapper.ChangeKind.Addon,
                    Action = result.WasCreated ? JsonMapper.ChangeAction.Added : JsonMapper.ChangeAction.Unchanged,
                    To = result.ManifestPath,
                },
            ],
            Messages = [message],
        };

        return ValueTask.FromResult(McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, payload))));
    }
}
