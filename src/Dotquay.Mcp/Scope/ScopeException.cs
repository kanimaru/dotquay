namespace Dotquay.Mcp.Scope;

/// <summary>A tool call named a project directory outside every root the MCP server was started
/// with. Caught by <c>Dotquay.Mcp.Tools.McpToolBase</c> and reported as an ordinary tool failure —
/// never a crash — with <c>Dotquay.Mcp.Tools.McpJsonErrorKind.Scope</c> so a client can tell "wrong
/// path" from every other failure category.</summary>
public sealed class ScopeException(string message) : Exception(message);
