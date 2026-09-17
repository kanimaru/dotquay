using Dotquay.Core.Abstractions;

namespace Dotquay.Mcp.Guards;

/// <summary>
/// Generator privacy → <c>keep_generator_private</c> (docs/DESIGN-v0.18-MCP.md §5.2).
/// <see cref="IGeneratorAssetDecision"/> likewise: default <see cref="ShareGeneratorDecision"/> (NuGet's
/// own behaviour, the <c>--json</c> default), <see cref="KeepGeneratorPrivateDecision"/> when the flag
/// is true. Nothing is added to Core.
/// </summary>
/// <remarks>
/// Unlike the other two guard policies, this one never refuses anything — there is no wrong answer to
/// "should a generator be private", only an unanswered one, and an unanswered one already has a safe
/// default. It still implements <see cref="IWritePolicy"/> (always allowing) so it lives alongside its
/// siblings in the same chain and under the same test discipline, per the design doc's own framing of
/// the three as <see cref="IWriteGuard"/>'s initial members; its real job is <see cref="Resolve"/>.
/// </remarks>
public sealed class GeneratorPrivacyPolicy : IWritePolicy
{
    public string Name => "generator_privacy";

    public ValueTask<WriteGuardVerdict> EvaluateAsync(WriteGuardContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(WriteGuardVerdict.Allow());

    /// <summary>Builds the <see cref="IGeneratorAssetDecision"/> a write tool wires into its real
    /// workflow for <c>dotquay_add</c>/<c>dotquay_add_dotnet</c>.</summary>
    public static IGeneratorAssetDecision Resolve(bool keepGeneratorPrivate) =>
        keepGeneratorPrivate ? KeepGeneratorPrivateDecision.Instance : ShareGeneratorDecision.Instance;
}
