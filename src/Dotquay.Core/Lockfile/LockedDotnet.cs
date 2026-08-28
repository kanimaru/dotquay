namespace Dotquay.Core.Lockfile;

/// <summary>One resolved <c>[[dotnet]]</c> entry (SPEC.md §7.3).</summary>
public sealed class LockedDotnet
{
    public required string Name { get; init; }

    public required string Resolved { get; init; }

    /// <summary>
    /// The declared <c>[nuget.sources.*]</c> name this package resolved from. Null (and omitted from
    /// the file) for nuget.org, so lockfiles written before v0.7 read unchanged.
    /// </summary>
    /// <remarks>
    /// This is what makes ordered multi-feed resolution reproducible rather than merely deterministic
    /// on one machine (SPEC.md §11.1). Without it, a package resolved from a private feed here could
    /// come from a public feed on a teammate's machine — same name, same version range, different
    /// code — purely because their manifest listed the feeds in a different order or one feed was
    /// temporarily unreachable.
    /// </remarks>
    public string? Source { get; init; }
}
