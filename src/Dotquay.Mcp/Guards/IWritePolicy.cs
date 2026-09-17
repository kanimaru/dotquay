namespace Dotquay.Mcp.Guards;

/// <summary>
/// One thing the console CLI would have asked a human about, reproduced as a structured
/// refuse-or-allow decision for a caller that has no human to ask (docs/DESIGN-v0.18-MCP.md §5).
/// </summary>
/// <remarks>
/// A plain interface with concrete classes in <c>Dotquay.Mcp.Guards</c>, not a switch statement in
/// <see cref="IWriteGuard"/>: adding a fourth policy (the design doc's own example — refusing to
/// remove an addon something else depends on, once the graph exposes reverse edges) is a new class
/// implementing this interface, never an edit to existing guard logic.
/// </remarks>
public interface IWritePolicy
{
    /// <summary>Stable machine name for this policy, echoed in a refusal's structured detail so an
    /// agent that has seen one refusal recognizes the same policy behind another tool's.</summary>
    string Name { get; }

    ValueTask<WriteGuardVerdict> EvaluateAsync(WriteGuardContext context, CancellationToken cancellationToken);
}
