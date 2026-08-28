namespace Dotquay.Core.Manifest;

/// <summary>One <c>[dotnet.&lt;PackageId&gt;]</c> entry. <see cref="Name"/> is the full NuGet
/// package ID, including dots (e.g. <c>"Newtonsoft.Json"</c>) — see
/// <see cref="Manifest.ManifestReader"/> for why reading these back out of TOML's dotted-table
/// syntax needs a recursive walk.</summary>
public sealed class DotnetSpec
{
    public required string Name { get; init; }

    /// <summary>Delegated to NuGet/<c>dotnet restore</c> verbatim, e.g. <c>"13.0.*"</c> (SPEC.md §11).</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Pins this package to one declared <c>[nuget.sources.*]</c> by name (SPEC.md §7.1). Null
    /// searches every source in declared order.
    /// </summary>
    /// <remarks>
    /// Told apart from a dotted package ID by TOML type, not by name: <c>source = "core"</c> is a
    /// string and means this, while <c>[dotnet.Foo.source]</c> is a table and means a package called
    /// <c>Foo.source</c>. A key cannot be both in TOML, so the two never collide — at the price that
    /// a package genuinely named <c>Foo.source</c> can't be pinned.
    /// </remarks>
    public string? Source { get; init; }
}
