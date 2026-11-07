using System.Text.Json;
using Dotquay.Core.Json;
using Dotquay.Mcp.Scope;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Tools;

/// <summary>
/// Shared plumbing for every dotquay MCP tool: resolves and scope-checks the <c>project</c> argument, serializes calls against that project through
/// <see cref="ProjectGate"/>, and turns any exception into the same failure shape
/// <c>Dotquay.Cli.Commands.JsonOutput.Fail</c> produces for the CLI.
/// </summary>
/// <remarks>
/// A tool subclass implements only <see cref="ExecuteAsync"/> and never sees a raw, unresolved path or
/// an uncaught exception — the parts every one of the read and write tools would otherwise duplicate
/// live here exactly once.
/// </remarks>
public abstract class McpToolBase(IProjectScope scope, ProjectGate gate) : IMcpTool
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonElement InputSchema { get; }

    public async ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var arguments = context.Params?.Arguments;

        try
        {
            var projectDirectory = scope.Resolve(McpArgs.GetString(arguments, "project"));
            var progress = new McpProgressReporter(context.Server, context.Params?.ProgressToken);

            return await gate.RunAsync(projectDirectory, () => ExecuteAsync(projectDirectory, arguments, progress, cancellationToken));
        }
        catch (ScopeException ex)
        {
            return McpToolResults.Failure(Name, McpJsonErrorKind.Scope, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var (kind, message) = JsonErrorClassifier.Classify(ex);
            return McpToolResults.Failure(Name, kind, message);
        }
    }

    /// <param name="projectDirectory">Already resolved and scope-checked — always an absolute path
    /// under one of the server's configured <c>--project</c> roots.</param>
    /// <param name="arguments">The call's raw arguments, for anything beyond <c>project</c> the tool
    /// itself defines (e.g. <c>explain</c>, <c>resolve</c>). Read with <see cref="McpArgs"/>.</param>
    /// <param name="progress">Forwards to the client when the call carried a progress token; a no-op
    /// otherwise. Safe to pass into <c>DependencyWorkflow.WithProgress</c> or ignore.</param>
    protected abstract ValueTask<CallToolResult> ExecuteAsync(
        string projectDirectory,
        IDictionary<string, JsonElement>? arguments,
        Dotquay.Core.Progress.IProgressReporter progress,
        CancellationToken cancellationToken);
}
