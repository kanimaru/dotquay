using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Prompts;

/// <summary>
/// One MCP prompt: its advertised shape (<see cref="Name"/>, <see cref="Description"/>,
/// <see cref="Arguments"/>) plus the handler that renders it.
/// </summary>
/// <remarks>
/// Mirrors <see cref="Tools.IMcpTool"/> exactly, for the same reason: a plain object
/// <see cref="PromptRegistry"/> can enumerate and dispatch to by name, never attribute-based
/// discovery (docs/DESIGN-v0.18-MCP.md §3.1) — there is no <c>[McpServerPrompt]</c> anywhere in
/// this codebase for a future contributor to accidentally widen into.
/// </remarks>
public interface IMcpPrompt {
    /// <summary>The wire name a client requests, e.g. <c>"dotquay_diagnose_modified"</c>.</summary>
    string Name { get; }

    /// <summary>Shown to the model or human deciding whether to use this prompt.</summary>
    string Description { get; }

    /// <summary>Empty when the prompt takes no arguments.</summary>
    IReadOnlyList<PromptArgument> Arguments { get; }

    ValueTask<GetPromptResult> GetAsync(RequestContext<GetPromptRequestParams> context,
        CancellationToken cancellationToken);
}
