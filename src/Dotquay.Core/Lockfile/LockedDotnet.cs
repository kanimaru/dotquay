namespace Dotquay.Core.Lockfile;

/// <summary>One resolved <c>[[dotnet]]</c> entry (SPEC.md §7.3).</summary>
public sealed class LockedDotnet {
    public required string Name { get; init; }

    public required string Resolved { get; init; }

    /// <summary>
    /// The declared <c>[nuget.sources.*]</c> name this package resolved from. Null (and omitted
    /// from the file) for nuget.org, so lockfiles written before v0.7 read unchanged.
    /// </summary>
    /// <remarks>
    /// This is what makes ordered multi-feed resolution reproducible rather than merely
    /// deterministic on one machine (SPEC.md §11.1). Without it, a package resolved from a private
    /// feed here could come from a public feed on a teammate's machine — same name, same version
    /// range, different code — purely because their manifest listed the feeds in a different order
    /// or one feed was temporarily unreachable.
    /// </remarks>
    public string? Source { get; init; }

    /// <summary>
    /// The asset metadata dotquay itself wrote into the <c>.csproj</c> for this package (v0.10).
    /// Omitted from the file when nothing was written, so lockfiles from before v0.10 read
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// This is not a duplicate of the manifest's declaration — it is the record of what dotquay
    /// <em>did</em>, and the two differing is exactly what makes removal possible. Without it,
    /// "the manifest stopped declaring private_assets" and "the user hand-added
    /// &lt;PrivateAssets&gt;" are the same observation, and dotquay would have to either strip
    /// hand-edits or leave stale metadata the manifest disclaims. It also keeps an addon-declared
    /// analyzer reproducible: the consuming project's own manifest never mentions that package.
    /// </remarks>
    public Manifest.PackageAssets Assets { get; init; } = Manifest.PackageAssets.None;

    /// <summary>
    /// This entry with its resolved-feed record cleared — what <c>source remove --force</c> writes
    /// so no lockfile entry is left pointing at a feed the project no longer declares.
    /// </summary>
    /// <remarks>
    /// A method here rather than a copy at the call site, for the reason
    /// <see cref="Lockfile.LockedAddon"/> learned the hard way: a hand-written copy of a class with
    /// init-only properties silently drops whichever property is added next.
    /// </remarks>
    public LockedDotnet WithoutSource() => new() {
        Name = Name,
        Resolved = Resolved,
        Source = null,
        Assets = Assets,
    };
}
