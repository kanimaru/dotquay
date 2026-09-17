using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Core.Progress;
using Dotquay.Mcp.Guards;
using Dotquay.Mcp.Infrastructure;
using Dotquay.Mcp.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp.Tools;

/// <summary>MCP mirror of <c>dotquay add &lt;package-id&gt;</c>: resolves the latest (or an explicit)
/// version of a NuGet package, wires it into the project's <c>.csproj</c>, and restores (SPEC.md §11).
/// See <c>Dotquay.Cli.Commands.AddCommand.AddDotnetAsync</c>.</summary>
/// <remarks>
/// No feed adoption here — a NuGet add resolves from feeds the project already declares, matching why
/// <c>AddCommand.cs</c> rejects <c>--preview</c> for a NuGet source (there is no dependency graph or
/// feed question to preview). <see cref="GeneratorPrivacyPolicy"/> still applies: the package itself
/// might be the generator.
/// </remarks>
public sealed class DotquayAddDotnetTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate)
{
    public override string Name => "dotquay_add_dotnet";

    public override string Description =>
        "Add a NuGet package: resolve a version, add a PackageReference, restore. Read-write.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "package_id": { "type": "string", "description": "The NuGet package ID to add." },
            "version": { "type": "string", "description": "An exact package version. Defaults to the latest stable release." },
            "nuget_source": {
              "type": "string",
              "description": "A declared [nuget.sources.<name>] name to resolve from, instead of searching every declared feed in order."
            },
            "keep_generator_private": {
              "type": "boolean",
              "description": "If this package is a source generator, keep it out of anything that depends on this project.",
              "default": false
            }
          },
          "required": ["package_id"],
          "additionalProperties": false
        }
        """).RootElement;

    protected override async ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken)
    {
        var packageId = McpArgs.GetString(arguments, "package_id");

        if (string.IsNullOrWhiteSpace(packageId))
        {
            return McpToolResults.Failure(Name, JsonErrorKind.Workflow, "'package_id' is required: the NuGet package to add.");
        }

        var version = McpArgs.GetString(arguments, "version");
        var nuGetSource = McpArgs.GetString(arguments, "nuget_source");
        var keepGeneratorPrivate = McpArgs.GetBool(arguments, "keep_generator_private");

        var workflow = McpInfrastructure.CreateWorkflow(
            progress, sourceApproval: null, GeneratorPrivacyPolicy.Resolve(keepGeneratorPrivate));

        var locked = await workflow.AddDotnetAsync(projectDirectory, packageId, version, nuGetSource, cancellationToken);

        var pinnedTo = nuGetSource is null ? "" : $", pinned to '{nuGetSource}'";
        var kept = locked.Assets.Private is null ? "" : ", kept private to this project";
        var summary = $"Added '{locked.Name}' @ {locked.Resolved} (NuGet) -> .csproj, restored{pinnedTo}{kept}";

        var payload = new OperationJson
        {
            Changes =
            [
                new ChangeJson
                {
                    Name = locked.Name,
                    Kind = JsonMapper.ChangeKind.Dotnet,
                    Action = JsonMapper.ChangeAction.Added,
                    To = locked.Resolved,
                },
            ],
            Messages = [summary],
        };

        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, payload)));
    }
}
