using Dotquay.Core.IO;
using Dotquay.Core.Json;
using Dotquay.Mcp.Resources;
using Dotquay.Mcp.Scope;
using Dotquay.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Dotquay.Mcp;

/// <summary>
/// Assembles the Generic Host that hosts dotquay's MCP server: every tool, the manifest/lockfile
/// resources, and the transport.
/// </summary>
/// <remarks>
/// The <see cref="IHost"/> is built here but never run here — <see cref="RunStdioAsync"/> is the one
/// production entry point (called from <c>dotquay mcp</c>), and <see cref="BuildForStreams"/> exists
/// so a test can drive the exact same tool/resource wiring over an in-memory pipe instead of real
/// stdio (design doc §8, test layer 1: "Handler conformance — WithStreamServerTransport"). Both share
/// <see cref="Configure"/>, so a test that passes never means the stdio path was untested — it means
/// the one difference between them (which <c>Stream</c>s the transport reads and writes) is the only
/// thing not exercised without a real published binary, which is what each phase's exit criterion
/// (Inspector + a real AOT publish) covers instead (docs/DESIGN-v0.18-MCP.md §9).
/// </remarks>
public static class McpServerBuilder
{
    public static async Task RunStdioAsync(IReadOnlyList<string> projectRoots, CancellationToken cancellationToken = default)
    {
        using var host = Configure(projectRoots, stdio: true, input: null, output: null);
        await host.RunAsync(cancellationToken);
    }

    /// <summary>For tests: the same tool/resource wiring as production, driven over an in-memory pipe
    /// instead of real stdio.</summary>
    public static IHost BuildForStreams(IReadOnlyList<string> projectRoots, Stream input, Stream output) =>
        Configure(projectRoots, stdio: false, input, output);

    private static IHost Configure(IReadOnlyList<string> projectRoots, bool stdio, Stream? input, Stream? output)
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
        var fileSystem = new SystemFileSystem();
        var toolRegistry = new ToolRegistry(BuildTools(scope, gate));
        var resourceRegistry = new ResourceRegistry(scope.Roots, fileSystem, scope);

        var mcpBuilder = builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "dotquay", Version = DotquayVersion.Current };
            })
            .WithListToolsHandler(toolRegistry.ListAsync)
            .WithCallToolHandler(toolRegistry.CallAsync)
            .WithListResourcesHandler(resourceRegistry.ListAsync)
            .WithReadResourceHandler(resourceRegistry.ReadAsync);

        if (stdio)
        {
            mcpBuilder.WithStdioServerTransport();
        }
        else
        {
            mcpBuilder.WithStreamServerTransport(input!, output!);
        }

        return builder.Build();
    }

    /// <summary>The eight Phase 1 read tools plus Phase 2's eleven guarded write tools
    /// (docs/DESIGN-v0.18-MCP.md §4.1/§4.2). Adding another is one line here —
    /// <see cref="Tools.ToolRegistry"/> needs no other change.</summary>
    private static IReadOnlyList<IMcpTool> BuildTools(IProjectScope scope, ProjectGate gate) =>
    [
        // Reads (Phase 1).
        new DotquayStatusTool(scope, gate),
        new DotquayListTool(scope, gate),
        new DotquayGraphTool(scope, gate),
        new DotquayOutdatedTool(scope, gate),
        new DotquayDoctorTool(scope, gate),
        new DotquayAddPreviewTool(scope, gate),
        new DotquaySourceListTool(scope, gate),
        new DotquayOverrideListTool(scope, gate),

        // Guarded writes (Phase 2).
        new DotquayInitTool(scope, gate),
        new DotquayAddTool(scope, gate),
        new DotquayAddDotnetTool(scope, gate),
        new DotquayInstallTool(scope, gate),
        new DotquayUpdateTool(scope, gate),
        new DotquayRemoveTool(scope, gate),
        new DotquayPatchCaptureTool(scope, gate),
        new DotquayPatchResolveTool(scope, gate),
        new DotquayPatchAbandonTool(scope, gate),
        new DotquayOverrideSetTool(scope, gate),
        new DotquayOverrideClearTool(scope, gate),
    ];
}
