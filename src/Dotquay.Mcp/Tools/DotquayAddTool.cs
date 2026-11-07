using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Core.Workflows;
using Dotquay.Mcp.Guards;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay add &lt;git-url&gt;</c>: resolves, locks, and installs a git
/// addon (SPEC.md §6). See <c>Dotquay.Cli.Commands.AddCommand</c>.
/// </summary>
/// <remarks>
/// Git sources only — <see cref="DotquayAddDotnetTool"/> is the NuGet twin, matching the design's
/// split of one CLI command into two tools (docs/DESIGN-v0.18-MCP.md §4.2). Like
/// <see cref="DotquayAddPreviewTool"/>, this tool does not accept the CLI's <c>&lt;url&gt;#ref</c>
/// shorthand: an agent names <c>version</c>/<c>branch</c>/<c>rev</c> explicitly instead. Guarded by
/// <see cref="SourceApprovalPolicy"/> (a git addon can declare NuGet feeds transitively) and
/// <see cref="GeneratorPrivacyPolicy"/> (it can also pull in a source generator) — never by
/// <see cref="ForceConfirmationPolicy"/>, which the table (§4.2) reserves for
/// <c>dotquay_install</c>/ <c>dotquay_update</c>; <c>AddonAddOptions.Force</c> is a different,
/// narrower escape hatch (an addon occupying its folder without a matching lock entry) that this
/// tool does not expose, matching the table's own argument list.
/// </remarks>
public sealed class DotquayAddTool(IProjectScope scope, ProjectGate gate)
    : McpToolBase(scope, gate) {
    public override string Name => "dotquay_add";

    public override string Description =>
        "Add a git addon: resolve it, lock it, install it into addons/. May refuse with " +
        "needs_confirmation if the addon declares a NuGet feed this project hasn't agreed to "
        + "trust " +
        "(retry with approved_source_urls) — call dotquay_add_preview first to see what it would "
        + "do.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "git_url": { "type": "string", "description": "The addon's git repository URL." },
            "version": { "type": "string", "description": "A tag or version constraint, e.g. \"~1.4\"." },
            "branch": { "type": "string", "description": "A branch to track; the lockfile still pins the resolved commit." },
            "rev": { "type": "string", "description": "An exact commit." },
            "path": { "type": "string", "description": "Subtree to extract, if the addon isn't at the repo root." },
            "install_as": { "type": "string", "description": "Folder to install into under addons/, if it shouldn't be the addon's own name." },
            "name": { "type": "string", "description": "The [addons.<name>] key to write instead of the repository's name. Rarely needed: a second addon from a repository whose name is taken is named after install_as or the last segment of path automatically." },
            "nuget_source": {
              "type": "string",
              "description": "Not valid here — NuGet feeds only apply to dotquay_add_dotnet. Setting this is refused, matching the CLI."
            },
            "approved_source_urls": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Feed URLs, as reported by a prior dotquay_add_preview or a needs_confirmation refusal, to approve for this add."
            },
            "keep_generator_private": {
              "type": "boolean",
              "description": "If this addon pulls in a NuGet source generator, keep it out of anything that depends on this project.",
              "default": false
            }
          },
          "required": ["git_url"],
          "additionalProperties": false
        }
        """).RootElement;

    protected override async ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory, IDictionary<string, JsonElement>? arguments,
        IProgressReporter progress, CancellationToken cancellationToken) {
        var gitUrl = McpArgs.GetString(arguments, "git_url");

        if (string.IsNullOrWhiteSpace(gitUrl)) {
            return McpToolResults.Failure(Name, JsonErrorKind.Workflow,
                "'git_url' is required: the repository URL to add.");
        }

        if (McpArgs.GetString(arguments, "nuget_source") is not null) {
            return McpToolResults.Failure(
                Name, JsonErrorKind.Workflow,
                $"'nuget_source' names a NuGet feed, but '{gitUrl}' is a git URL added by this "
                + "tool. NuGet feeds only apply to dotquay_add_dotnet.");
        }

        var options = new AddonAddOptions {
            Version = McpArgs.GetString(arguments, "version"),
            Branch = McpArgs.GetString(arguments, "branch"),
            Rev = McpArgs.GetString(arguments, "rev"),
            Path = McpArgs.GetString(arguments, "path"),
            InstallAs = McpArgs.GetString(arguments, "install_as"),
            Name = McpArgs.GetString(arguments, "name"),
        };

        try {
            options.Validate();
        } catch (WorkflowException ex) {
            return McpToolResults.Failure(Name, JsonErrorKind.Workflow, ex.Message);
        }

        var approvedSourceUrls = McpArgs.GetStringArray(arguments, "approved_source_urls");
        var keepGeneratorPrivate = McpArgs.GetBool(arguments, "keep_generator_private");

        var guardContext = new WriteGuardContext {
            ProjectDirectory = projectDirectory,
            InspectionWorkflow = McpInfrastructure.CreateWorkflow(),
            AddPreview = new AddSourcePreviewRequest(gitUrl, options),
            ApprovedSourceUrls = approvedSourceUrls,
        };

        var verdict = await WriteGuard.Default.EvaluateAsync(guardContext, cancellationToken);

        if (verdict is WriteGuardRefusal refusal) {
            return McpToolResults.FromRefusal(Name, refusal);
        }

        var workflow = McpInfrastructure.CreateWorkflow(
            progress, SourceApprovalPolicy.Resolve(approvedSourceUrls),
            GeneratorPrivacyPolicy.Resolve(keepGeneratorPrivate));

        var locked = await workflow.AddAsync(projectDirectory, gitUrl, options, cancellationToken);

        // Pseudo-version means no explicit source was given and the default fell back to pinning
        // the default branch's tip (SPEC.md §6) — see AddCommand.AddGitAsync's identical check.
        var untagged = !options.HasExplicitSource
            && locked.Version is not null
            && locked.Version.StartsWith("0.0.0-", StringComparison.Ordinal);

        var summary = $"Added '{locked.Name}' @ {locked.Version} ({ShortSha(locked.Resolved)}) -> "
            + $"addons/{locked.EffectiveFolder}";

        var hint = $"'{locked.Name}' has no git tags — pinned to its current default-branch commit "
            + $"instead of a version range. Add it with branch: \"<name>\" to track a branch "
            + $"instead, or call dotquay_update later to move to a newer commit.";

        var payload = new OperationJson {
            Changes =
            [
                new ChangeJson
                {
                    Name = locked.Name,
                    Kind = JsonMapper.ChangeKind.Addon,
                    Action = JsonMapper.ChangeAction.Added,
                    To = locked.Version ?? ShortSha(locked.Resolved),
                },
            ],
            Messages = [summary],
        };

        return McpToolResults.FromJson(
            DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, payload, untagged
                ? [hint] : null)));
    }

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
