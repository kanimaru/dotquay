namespace Dependi.Core.Manifest;

/// <summary>One <c>[dotnet.&lt;PackageId&gt;]</c> entry. <see cref="Name"/> is the full NuGet
/// package ID, including dots (e.g. <c>"Newtonsoft.Json"</c>) — see
/// <see cref="Manifest.ManifestReader"/> for why reading these back out of TOML's dotted-table
/// syntax needs a recursive walk.</summary>
public sealed class DotnetSpec
{
    public required string Name { get; init; }

    /// <summary>Delegated to NuGet/<c>dotnet restore</c> verbatim, e.g. <c>"13.0.*"</c> (SPEC.md §11).</summary>
    public required string Version { get; init; }
}
