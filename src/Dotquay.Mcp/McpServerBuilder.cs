using System.Text;
using Dotquay.Core.IO;
using Dotquay.Core.Json;
using Dotquay.Mcp.Logging;
using Dotquay.Mcp.Prompts;
using Dotquay.Mcp.Resources;
using Dotquay.Mcp.Scope;
using Dotquay.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp;

/// <summary>
/// Assembles the Generic Host that hosts dotquay's MCP server: every tool, prompt, and resource, the
/// client-roots sync, the request log, and the transport.
/// </summary>
/// <remarks>
/// The <see cref="IHost"/> is built here but never run here — <see cref="RunStdioAsync"/> is the one
/// production entry point (called from <c>dotquay mcp</c>), and <see cref="BuildForStreams"/> exists
/// so a test can drive the exact same tool/resource/prompt wiring over an in-memory pipe instead of
/// real stdio (design doc §8, test layer 1: "Handler conformance — WithStreamServerTransport"). Both
/// share <see cref="Configure"/>, so a test that passes never means the stdio path was untested — it
/// means the one difference between them (which <c>Stream</c>s the transport reads and writes) is the
/// only thing not exercised without a real published binary, which is what each phase's exit criterion
/// (Inspector + a real AOT publish) covers instead (docs/DESIGN-v0.18-MCP.md §9).
/// </remarks>
public static class McpServerBuilder
{
    public static async Task RunStdioAsync(IReadOnlyList<string> projectRoots, string? logFile = null, CancellationToken cancellationToken = default)
    {
        using var host = Configure(projectRoots, stdio: true, input: null, output: null, logFile);
        await host.RunAsync(cancellationToken);
    }

    /// <summary>For tests: the same tool/resource/prompt wiring as production, driven over an
    /// in-memory pipe instead of real stdio.</summary>
    public static IHost BuildForStreams(IReadOnlyList<string> projectRoots, Stream input, Stream output, string? logFile = null) =>
        Configure(projectRoots, stdio: false, input, output, logFile);

    private static IHost Configure(IReadOnlyList<string> projectRoots, bool stdio, Stream? input, Stream? output, string? logFile)
    {
        var builder = Host.CreateApplicationBuilder();

        // stdout is the wire (design doc §3.2): Host.CreateApplicationBuilder registers a console
        // logger by default, and a single ordinary log line on stdout would corrupt every JSON-RPC
        // frame written after it. Confirmed the hard way in the Phase 0 spike (a real AOT publish
        // showed the console logger active until this call was added) — this is not optional, and a
        // future change to this method must keep it as the first thing that touches `builder`.
        builder.Logging.ClearProviders();

        var scope = new ProjectScope(projectRoots);
        var gate = new ProjectGate();
        var rootsSync = new ClientRootsSync(scope);
        var fileSystem = new SystemFileSystem();
        var toolRegistry = new ToolRegistry(BuildTools(scope, gate, rootsSync));
        var resourceRegistry = new ResourceRegistry(fileSystem, scope);
        var promptRegistry = new PromptRegistry(BuildPrompts());

        var levelState = new LoggingLevelState();
        var (logWriter, ownsLogWriter) = OpenLogWriter(logFile);
        var requestLog = new RequestLog(logWriter, ownsLogWriter, levelState);

        var mcpBuilder = builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "dotquay", Version = DotquayVersion.Current };
            })
            .WithListToolsHandler(toolRegistry.ListAsync)
            .WithCallToolHandler(toolRegistry.CallAsync)
            .WithListResourcesHandler(resourceRegistry.ListAsync)
            .WithReadResourceHandler(resourceRegistry.ReadAsync)
            .WithListPromptsHandler(promptRegistry.ListAsync)
            .WithGetPromptHandler(promptRegistry.GetAsync)
            .WithSetLoggingLevelHandler(levelState.HandleSetLevelAsync)
            .WithRequestFilters(filters =>
            {
                // One filter per request kind this server actually handles (design doc §6): every
                // request id, method, and duration goes to `requestLog`, win or lose, and a failure
                // additionally reaches the client as `notifications/message` when it asked for that
                // severity via `logging/setLevel`. RequestLoggingFilters.Create is the one place that
                // logic lives — see its own remarks for why this isn't seven near-identical filters.
                filters.AddListToolsFilter(RequestLoggingFilters.Create<ListToolsRequestParams, ListToolsResult>("tools/list", requestLog));
                filters.AddCallToolFilter(RequestLoggingFilters.Create<CallToolRequestParams, CallToolResult>("tools/call", requestLog));
                filters.AddListResourcesFilter(RequestLoggingFilters.Create<ListResourcesRequestParams, ListResourcesResult>("resources/list", requestLog));
                filters.AddReadResourceFilter(RequestLoggingFilters.Create<ReadResourceRequestParams, ReadResourceResult>("resources/read", requestLog));
                filters.AddListPromptsFilter(RequestLoggingFilters.Create<ListPromptsRequestParams, ListPromptsResult>("prompts/list", requestLog));
                filters.AddGetPromptFilter(RequestLoggingFilters.Create<GetPromptRequestParams, GetPromptResult>("prompts/get", requestLog));
                filters.AddSetLoggingLevelFilter(RequestLoggingFilters.Create<SetLevelRequestParams, EmptyResult>("logging/setLevel", requestLog));
            });

        if (stdio)
        {
            mcpBuilder.WithStdioServerTransport();
        }
        else
        {
            mcpBuilder.WithStreamServerTransport(input!, output!);
        }

        var host = builder.Build();

        // `requestLog` is constructed directly (not resolved from DI), so nothing disposes it
        // automatically — a pre-created singleton instance registered with the container is *not*
        // disposed by it (the container assumes the caller who built it owns its lifetime). Hooking
        // ApplicationStopping is what ties its lifetime to the host's without handing DI an instance
        // it would otherwise leave dangling: every write is already flushed immediately (see
        // RequestLog.WriteLine), so this is about closing the file handle cleanly, not about losing
        // data if it never ran.
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(requestLog.Dispose);

        return host;
    }

    /// <summary>The eight Phase 1 read tools plus Phase 2's eleven guarded write tools
    /// (docs/DESIGN-v0.18-MCP.md §4.1/§4.2). Adding another is one line here —
    /// <see cref="Tools.ToolRegistry"/> needs no other change.</summary>
    private static IReadOnlyList<IMcpTool> BuildTools(IProjectScope scope, ProjectGate gate, ClientRootsSync rootsSync) =>
    [
        // Reads (Phase 1).
        new DotquayStatusTool(scope, gate, rootsSync),
        new DotquayListTool(scope, gate, rootsSync),
        new DotquayGraphTool(scope, gate, rootsSync),
        new DotquayOutdatedTool(scope, gate, rootsSync),
        new DotquayDoctorTool(scope, gate, rootsSync),
        new DotquayAddPreviewTool(scope, gate, rootsSync),
        new DotquaySourceListTool(scope, gate, rootsSync),
        new DotquayOverrideListTool(scope, gate, rootsSync),

        // Guarded writes (Phase 2).
        new DotquayInitTool(scope, gate, rootsSync),
        new DotquayAddTool(scope, gate, rootsSync),
        new DotquayAddDotnetTool(scope, gate, rootsSync),
        new DotquayInstallTool(scope, gate, rootsSync),
        new DotquayUpdateTool(scope, gate, rootsSync),
        new DotquayRemoveTool(scope, gate, rootsSync),
        new DotquayPatchCaptureTool(scope, gate, rootsSync),
        new DotquayPatchResolveTool(scope, gate, rootsSync),
        new DotquayPatchAbandonTool(scope, gate, rootsSync),
        new DotquayOverrideSetTool(scope, gate, rootsSync),
        new DotquayOverrideClearTool(scope, gate, rootsSync),
    ];

    /// <summary>The three Phase 3 prompts (docs/DESIGN-v0.18-MCP.md §7).</summary>
    private static IReadOnlyList<IMcpPrompt> BuildPrompts() =>
    [
        new DotquayDiagnoseModifiedPrompt(),
        new DotquayUpgradeSafelyPrompt(),
        new DotquayAddAddonPrompt(),
    ];

    /// <summary>Opens the <c>--log</c> destination: the named file, appended to, or
    /// <see cref="Console.Error"/> when no file was given (design doc §6). The returned flag says
    /// whether <see cref="RequestLog"/> owns (and must dispose) the writer — never true for
    /// <see cref="Console.Error"/>, which the rest of the process still needs after this server
    /// stops.</summary>
    private static (TextWriter Writer, bool OwnsWriter) OpenLogWriter(string? logFile)
    {
        if (string.IsNullOrWhiteSpace(logFile))
        {
            return (Console.Error, false);
        }

        var stream = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };

        return (writer, true);
    }
}
