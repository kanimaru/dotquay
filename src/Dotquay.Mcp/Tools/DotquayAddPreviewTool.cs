using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Core.Workflows;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay add --preview</c>: resolves what adding a git addon would do —
/// the full transitive graph, NuGet packages it brings in, and any feeds it would ask this project to
/// trust — without writing anything (SPEC.md §6, §11.3). See <c>Dotquay.Cli.Commands.AddCommand</c>.
/// </summary>
/// <remarks>
/// Git addons only, matching the CLI flag it mirrors: a NuGet add resolves one version from feeds the
/// project already declares, so there is no dependency graph or feed adoption to preview
/// (<c>AddCommand.cs</c> rejects <c>--preview</c> for a NuGet source for the same reason). Unlike the
/// CLI, this tool does not accept the <c>source#ref</c> shorthand — an agent can name
/// <paramref name="branch"/>/<paramref name="rev"/>/<paramref name="version"/> explicitly, which is
/// unambiguous where a shared parsing helper would otherwise have to be duplicated from
/// <c>Dotquay.Cli</c> (internal there) for a convenience a structured caller doesn't need.
/// </remarks>
public sealed class DotquayAddPreviewTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate) {
    public override string Name => "dotquay_add_preview";

    public override string Description =>
        "Resolve what adding a git addon would do: the transitive addon graph, NuGet packages it brings in, "
        + "and any NuGet feeds it would ask this project to trust. Read-only: writes nothing. Git sources only.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "git": {
              "type": "string",
              "description": "The addon's git repository URL."
            },
            "version": { "type": "string", "description": "A tag or version constraint, e.g. \"~1.4\"." },
            "branch": { "type": "string", "description": "A branch to track; the lockfile still pins the resolved commit." },
            "rev": { "type": "string", "description": "An exact commit." },
            "path": { "type": "string", "description": "Subtree to extract, if the addon isn't at the repo root." },
            "install_as": { "type": "string", "description": "Folder to install into under addons/, if it shouldn't be the addon's own name." },
            "name": { "type": "string", "description": "The [addons.<name>] key to write instead of the repository's name. Rarely needed: a second addon from a repository whose name is taken is named after install_as or the last segment of path automatically." },
            "adopt_source": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Feed URLs, as reported by a prior preview's sources_needing_approval, to pre-approve for this preview."
            }
          },
          "required": ["git"],
          "additionalProperties": false
        }
        """).RootElement;

    protected override async ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken) {
        var git = McpArgs.GetString(arguments, "git");

        if (string.IsNullOrWhiteSpace(git)) {
            return McpToolResults.Failure(Name, JsonErrorKind.Workflow, "'git' is required: the repository URL to preview adding.");
        }

        var options = new AddonAddOptions {
            Version = McpArgs.GetString(arguments, "version"),
            Branch = McpArgs.GetString(arguments, "branch"),
            Rev = McpArgs.GetString(arguments, "rev"),
            Path = McpArgs.GetString(arguments, "path"),
            InstallAs = McpArgs.GetString(arguments, "install_as"),
            Name = McpArgs.GetString(arguments, "name"),
        };

        var adoptSources = McpArgs.GetStringArray(arguments, "adopt_source");
        var workflow = McpInfrastructure.CreateWorkflow(progress);

        var preview = await workflow.PreviewAddAsync(projectDirectory, git, options, adoptSources, cancellationToken);
        var payload = JsonMapper.ToJson(preview);

        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<AddPreviewJson>.Success(Name, payload, preview.Warnings)));
    }
}
