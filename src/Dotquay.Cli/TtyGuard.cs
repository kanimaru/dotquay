namespace Dotquay.Cli;

/// <summary>
/// Refuses <c>dotquay mcp</c> when stdin is a real, interactive console instead of the pipe an MCP
/// client spawns it with (docs/DESIGN-v0.18-MCP.md §12, OQ-2). Without this, someone who runs
/// <c>dotquay mcp</c> by hand sees nothing at all — the process is just sitting on a pending read of
/// a JSON-RPC message that will never arrive, which looks exactly like a hang, not like a program
/// waiting for the thing it was actually built to wait for.
/// </summary>
/// <remarks>
/// The decision itself (<see cref="ShouldRefuse"/>) is a pure function of one bool so it is testable
/// without touching the real console — an automated test cannot reproduce a genuine interactive TTY,
/// but it can assert the boolean logic directly. <see cref="Commands.McpCommand"/> is the one call
/// site that passes the real <see cref="System.Console.IsInputRedirected"/>.
/// </remarks>
public static class TtyGuard
{
    /// <summary>Exit code the design doc names for this specific refusal (OQ-2).</summary>
    public const int ExitCode = 2;

    /// <summary>Printed to stderr — <c>dotquay mcp</c> never writes anything else there before this
    /// check runs, so the message is the whole of what a confused user sees.</summary>
    public const string Message =
        "dotquay mcp expects JSON-RPC on stdin from an MCP client (Claude Code, Claude Desktop, "
            + "Cursor, or similar) — nothing will happen if you run it directly at a terminal. "
            + "Configure your client to launch `dotquay mcp --project <path>` instead.";

    /// <summary>True when stdin is a real, unredirected console — i.e. dotquay was started directly
    /// at a terminal rather than spawned by a client that pipes it JSON-RPC.</summary>
    public static bool ShouldRefuse(bool isInputRedirected) => !isInputRedirected;
}
