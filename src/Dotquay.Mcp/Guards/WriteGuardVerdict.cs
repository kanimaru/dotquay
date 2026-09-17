namespace Dotquay.Mcp.Guards;

/// <summary>
/// What one <see cref="IWritePolicy"/> decided about a write tool call: let it through, or refuse it
/// with exactly what the agent must show the user and pass back to retry (docs/DESIGN-v0.18-MCP.md
/// §5).
/// </summary>
/// <remarks>
/// <see cref="WriteGuardRefusal.Detail"/> is deliberately <c>object</c>, not a shared base type: every
/// policy that can refuse has its own detail shape (<c>ForceConfirmationDetailJson</c>,
/// <c>SourceApprovalDetailJson</c> — <see cref="Dotquay.Mcp.Json"/>), and the caller already knows
/// which policy it asked, so it can cast straight to the type it expects rather than the guard chain
/// inventing a lowest-common-denominator shape nobody needs.
/// </remarks>
public abstract record WriteGuardVerdict
{
    public static WriteGuardVerdict Allow() => WriteGuardAllow.Instance;

    public static WriteGuardVerdict Refuse(string message, object detail) => new WriteGuardRefusal(message, detail);
}

public sealed record WriteGuardAllow : WriteGuardVerdict
{
    public static readonly WriteGuardAllow Instance = new();
}

public sealed record WriteGuardRefusal(string Message, object Detail) : WriteGuardVerdict;
