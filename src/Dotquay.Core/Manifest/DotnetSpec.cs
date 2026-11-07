namespace Dotquay.Core.Manifest;

/// <summary>One <c>[dotnet.&lt;PackageId&gt;]</c> entry. <see cref="Name"/> is the full NuGet
/// package ID, including dots (e.g. <c>"Newtonsoft.Json"</c>) — see
/// <see cref="Manifest.ManifestReader"/> for why reading these back out of TOML's dotted-table
/// syntax needs a recursive walk.</summary>
public sealed class DotnetSpec {
    public required string Name { get; init; }

    /// <summary>A version constraint in dotquay's one language
    /// (<see cref="Versioning.VersionConstraint"/>, DESIGN-v0.21), e.g. <c>"^13.0"</c> or
    /// <c>"13.0.*"</c>. dotquay resolves it itself and writes only the exact result into the
    /// <c>.csproj</c>, so NuGet's range syntax never applies.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Pins this package to one declared <c>[nuget.sources.*]</c> by name (SPEC.md §7.1). Null
    /// searches every source in declared order.
    /// </summary>
    /// <remarks>
    /// Told apart from a dotted package ID by TOML type, not by name: <c>source = "core"</c> is a
    /// string and means this, while <c>[dotnet.Foo.source]</c> is a table and means a package
    /// called <c>Foo.source</c>. A key cannot be both in TOML, so the two never collide — at the
    /// price that a package genuinely named <c>Foo.source</c> can't be pinned.
    /// </remarks>
    public string? Source { get; init; }

    /// <summary>
    /// Declared <c>IncludeAssets</c>/<c>ExcludeAssets</c>/<c>PrivateAssets</c> for this package
    /// (v0.10). <see cref="PackageAssets.None"/> — the default — means the manifest declared none,
    /// and dotquay therefore leaves the <c>.csproj</c>'s asset elements alone.
    /// </summary>
    /// <remarks>
    /// Told apart from a dotted package ID the same way <see cref="Source"/> is, but by a different
    /// TOML type: these keys are <em>arrays</em>, while <c>[dotnet.Foo.include_assets]</c> would be
    /// a <em>table</em> naming a package called <c>Foo.include_assets</c>. One key cannot be both,
    /// so the two readings never collide.
    /// </remarks>
    public PackageAssets Assets { get; init; } = PackageAssets.None;

    /// <summary>
    /// This spec with its feed pin cleared — what <c>source remove --force</c> writes.
    /// </summary>
    /// <remarks>
    /// A method on the type rather than a copy expression at the call site, and this is not
    /// bookkeeping: <see cref="Lockfile.LockedAddon"/> was a class with hand-written copy helpers
    /// that silently dropped <c>Folder</c> when the field was added, which is exactly what an
    /// inline copy invites. Keeping the copy here means a new property is added three lines from
    /// the code that has to carry it.
    /// </remarks>
    public DotnetSpec WithoutSource() => new() {
        Name = Name,
        Version = Version,
        Source = null,
        Assets = Assets,
    };
}
