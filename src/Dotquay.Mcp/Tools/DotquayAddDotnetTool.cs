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
public sealed class DotquayAddDotnetTool(IProjectScope scope, ProjectGate gate) : McpToolBase(scope, gate) {
    public override string Name => "dotquay_add_dotnet";

    public override string Description =>
        "Add a NuGet package: resolve a version, add a PackageReference, restore. With a version, also pins a "
        + "package an addon brought in, or changes the version of one the project already declares. Read-write.";

    public override JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Absolute path to the project directory (containing dotquay.toml). Defaults to the server's first --project root."
            },
            "package_id": { "type": "string", "description": "The NuGet package ID to add." },
            "version": { "type": "string", "description": "A version constraint: an exact version (\"1.2.3\"), \"^1.2\", \"~1.2\", \"1.2.*\", \">=1.2 <2\" or a prerelease (\"5.1.0-rc1\"). The highest version inside it is installed, and it is written to the manifest as given. Defaults to \"^\" on the latest stable release. On a package the project already has, the range to pin it to or change it to." },
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
        string projectDirectory, IDictionary<string, JsonElement>? arguments, IProgressReporter progress, CancellationToken cancellationToken) {
        var packageId = McpArgs.GetString(arguments, "package_id");

        if (string.IsNullOrWhiteSpace(packageId)) {
            return McpToolResults.Failure(Name, JsonErrorKind.Workflow, "'package_id' is required: the NuGet package to add.");
        }

        var version = McpArgs.GetString(arguments, "version");
        var nuGetSource = McpArgs.GetString(arguments, "nuget_source");
        var keepGeneratorPrivate = McpArgs.GetBool(arguments, "keep_generator_private");

        var workflow = McpInfrastructure.CreateWorkflow(
            progress, sourceApproval: null, GeneratorPrivacyPolicy.Resolve(keepGeneratorPrivate));

        // Also pins an addon-declared package, or changes a declared one, when a version is given
        // (DESIGN-v0.19 §7a.1) — the same entry point `add` uses on the CLI.
        var declaration = await workflow.DeclareDotnetAsync(projectDirectory, packageId, version, nuGetSource, cancellationToken);

        var payload = new OperationJson {
            Changes = [JsonMapper.ToChange(declaration)],
            Messages = declaration.Messages(nuGetSource),
        };

        return McpToolResults.FromJson(DotquayMcpJson.Write(JsonEnvelope<OperationJson>.Success(Name, payload)));
    }
}
