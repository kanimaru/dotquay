namespace Dotquay.Mcp.Guards;

/// <summary>
/// The "would this have prompted a human at the console?" check every guarded write tool runs
/// before touching Core (docs/DESIGN-v0.18-MCP.md §5). Evaluates its policies in order and stops at
/// the first refusal, so a caller always sees the single most relevant thing to fix rather than
/// every policy's opinion at once.
/// </summary>
public interface IWriteGuard {
    ValueTask<WriteGuardVerdict> EvaluateAsync(WriteGuardContext context,
        CancellationToken cancellationToken);
}
