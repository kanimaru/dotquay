namespace Dotquay.Mcp.Tools;

/// <summary>
/// Error categories that exist only at the MCP boundary, alongside <see cref="Dotquay.Core.Json.JsonErrorKind"/>.
/// </summary>
/// <remarks>
/// Deliberately not added to <see cref="Dotquay.Core.Json.JsonErrorKind"/> itself: that type is part
/// of the Godot editor plugin's wire contract (SPEC.md §17), and <see cref="Scope"/> describes a
/// failure mode — a tool call naming a directory outside <c>--project</c> — that the CLI has no
/// equivalent of and the plugin will never see.
/// </remarks>
public static class McpJsonErrorKind
{
    /// <summary>The requested project path is outside every <c>--project</c> root this MCP server was
    /// started with. See <see cref="Dotquay.Mcp.Scope.ScopeException"/>.</summary>
    public const string Scope = "scope";
}
