namespace Dotquay.Core.Abstractions;

/// <summary>
/// Decides whether a source generator being added should be kept private to this project, or allowed
/// to flow on to whatever depends on it (docs/DESIGN-v0.14-FEED-COMMANDS.md).
/// </summary>
/// <remarks>
/// <para>A seam rather than a prompt inside the workflow, for the same reason
/// <see cref="ISourceApproval"/> is one: the Godot editor plugin drives dotquay through captured
/// stdout and cannot answer a question (SPEC.md §6.3/§18), and a workflow blocking on
/// <c>Console.ReadLine</c> is untestable. The CLI implements it as a prompt; <c>--json</c>, CI, and the
/// explicit flags use the two constant implementations below.</para>
/// <para>Only ever consulted when the package declared itself a development dependency <em>and</em>
/// the project looks like something other projects consume. For a Godot game — the overwhelmingly
/// common case — nothing depends on the project, so the question has no consequence and is never
/// asked.</para>
/// </remarks>
public interface IGeneratorAssetDecision {
    /// <summary>True to write <c>private_assets = ["all"]</c>, keeping the generator out of anything
    /// that consumes this project.</summary>
    bool KeepPrivate(GeneratorAssetRequest request);
}

/// <summary>
/// The facts behind the question: which generator, and why dotquay thinks the answer matters here.
/// </summary>
/// <param name="PackageId">The package being added.</param>
/// <param name="Version">The version resolved for it, so the prompt names something concrete.</param>
/// <param name="PackableEvidence">The <c>.csproj</c> property that made this project look publishable
/// — <c>PackageId</c>, <c>GeneratePackageOnBuild</c>, or <c>IsPackable</c>. Named in the prompt because
/// the probe is a heuristic, and a user who disagrees with it deserves to see what it read rather than
/// being asked an apparently arbitrary question about their game.</param>
public sealed record GeneratorAssetRequest(string PackageId, string Version, string PackableEvidence);

/// <summary>
/// Lets the generator flow onward — which is what NuGet does with no declaration at all.
/// </summary>
/// <remarks>
/// The default everywhere that cannot ask, and deliberately the one that writes nothing. Measured
/// 2026-09-10: a generator referenced with a plain <c>PackageReference</c> really does run in a
/// downstream consumer (verified three levels deep with a generated <em>internal</em> type, so the
/// consumer could only compile if the generator ran in the consumer itself). So this is not a
/// conservative guess about NuGet's behaviour — it is NuGet's behaviour, and choosing it keeps a
/// non-interactive <c>add</c> byte-identical to every release before v0.14.
/// </remarks>
public sealed class ShareGeneratorDecision : IGeneratorAssetDecision {
    public static readonly ShareGeneratorDecision Instance = new();

    public bool KeepPrivate(GeneratorAssetRequest request) => false;
}

/// <summary>Keeps the generator private — <c>add --keep-generator-private</c>, and the
/// editor plugin's checkbox once it has one.</summary>
public sealed class KeepGeneratorPrivateDecision : IGeneratorAssetDecision {
    public static readonly KeepGeneratorPrivateDecision Instance = new();

    public bool KeepPrivate(GeneratorAssetRequest request) => true;
}
