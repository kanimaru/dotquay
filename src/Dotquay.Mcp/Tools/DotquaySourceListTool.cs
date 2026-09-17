using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay source list</c>: the NuGet feeds this project searches, in order
/// (SPEC.md Â§7.1, Â§11.1). See <c>Dotquay.Cli.Commands.SourceCommand</c>.</summary>
public sealed class DotquaySourceListTool(IProjectScope scope, ProjectGate gate, ClientRootsSync rootsSync) : McpToolBase(scope, gate, rootsSync)
{
    public override string Name => "dotquay_source_list";

    public override string Description => "Show the declared NuGet feeds this project searches, in order. Read-only.";

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
        var sources = McpInfrastructure.CreateSources().List(projectDirectory);
        var credentials = McpInfrastructure.CreateCredentialResolver();

        var payload = new SourceListJson
        {
            Sources = sources
                .Select(source => new SourceJson
                {
                    Name = source.Name,
                    Url = source.Url,
                    Packages = source.Packages,
                    DeclaredByAddon = source.DeclaredByAddon,
                    HasCredential = !source.IsPublicDefault && credentials.ForNuGetSource(source.Name, source.Url).Credential is not null,
                })
                .ToList(),
        };

        return ValueTask.FromResult(McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<SourceListJson>.Success(Name, payload))));
    }
}
