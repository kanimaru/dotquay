namespace Dotquay.Mcp.Tools;

/// <summary>
/// Error categories that exist only at the MCP boundary, alongside <see cref="Dotquay.Core.Json.JsonErrorKind"/>.
/// </summary>
/// <remarks>
/// Deliberately not added to <see cref="Dotquay.Core.Json.JsonErrorKind"/> itself: that type is part
/// of the Godot editor plugin's wire contract (SPEC.md §17), and both <see cref="Scope"/> and
/// <see cref="NeedsConfirmation"/> describe failure modes the CLI has no equivalent of and the plugin
/// will never see.
/// </remarks>
public static class McpJsonErrorKind {
    /// <summary>The requested project path is outside every <c>--project</c> root this MCP server was
    /// started with. See <see cref="Dotquay.Mcp.Scope.ScopeException"/>.</summary>
    public const string Scope = "scope";

    /// <summary>A write policy refused the call (docs/DESIGN-v0.18-MCP.md §5.3/§5.4) — the same thing
    /// that would have prompted a human at the console. <c>detail.policy</c> names which one; the rest
    /// of <c>detail</c> is what the agent must show the user and pass back to retry. See
    /// <see cref="Dotquay.Mcp.Guards.IWriteGuard"/>.</summary>
    public const string NeedsConfirmation = "needs_confirmation";
}
