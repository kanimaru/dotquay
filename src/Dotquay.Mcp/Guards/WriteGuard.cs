namespace Dotquay.Mcp.Guards;

/// <summary>
/// The default <see cref="IWriteGuard"/>: a fixed, ordered chain of <see cref="IWritePolicy"/>
/// instances. Production always runs the same three (docs/DESIGN-v0.18-MCP.md §5); the ordered-list
/// constructor exists so a test can run one policy in isolation without the others' Core calls (e.g.
/// a fake project with no manifest would make <see cref="SourceApprovalPolicy"/>'s preview throw
/// before <see cref="ForceConfirmationPolicy"/> ever got a chance to matter).
/// </summary>
public sealed class WriteGuard(IReadOnlyList<IWritePolicy> policies) : IWriteGuard
{
    public static WriteGuard Default { get; } = new(
    [
        new SourceApprovalPolicy(),
        new GeneratorPrivacyPolicy(),
        new ForceConfirmationPolicy(),
    ]);

    public async ValueTask<WriteGuardVerdict> EvaluateAsync(WriteGuardContext context, CancellationToken cancellationToken)
    {
        foreach (var policy in policies)
        {
            var verdict = await policy.EvaluateAsync(context, cancellationToken);

            if (verdict is WriteGuardRefusal)
            {
                return verdict;
            }
        }

        return WriteGuardVerdict.Allow();
    }
}
