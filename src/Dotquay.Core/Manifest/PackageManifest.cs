namespace Dotquay.Core.Manifest;

/// <summary>The parsed contents of an addon-side <c>dotquay.toml</c> (SPEC.md §7.2) — same schema
/// as <see cref="ProjectManifest"/> plus a <c>[package]</c> header. Ships inside the addon's own
/// folder next to <c>plugin.cfg</c>, so an addon repo is itself a valid dotquay project. Third-party
/// addons without this file are leaf nodes in the dependency graph (SPEC.md §8).</summary>
public sealed class PackageManifest
{
    /// <summary>The package's own name. Required — it is the one piece of self-identification an
    /// addon carries, and the only way a manifest can later be checked against the name a consumer
    /// declared for it.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional, and read by nothing today.
    /// </summary>
    /// <remarks>
    /// It was required through v0.5 and never consulted: the version that decides resolution is the
    /// git tag, and the version <c>doctor</c> checks for drift is <c>plugin.cfg</c>'s. Being mandatory
    /// made it a third place to remember to bump, and the two that matter are already checked against
    /// each other. Kept as an optional field rather than deleted so existing package manifests parse
    /// unchanged and a future "declared version vs. tag" check has somewhere to live.
    /// </remarks>
    public string? Version { get; init; }

    public IReadOnlyDictionary<string, AddonSpec> Addons { get; init; } = new Dictionary<string, AddonSpec>();

    public IReadOnlyDictionary<string, DotnetSpec> Dotnet { get; init; } = new Dictionary<string, DotnetSpec>();

    /// <summary>
    /// NuGet feeds this addon's own <c>[dotnet]</c> dependencies come from (SPEC.md §11.3).
    /// </summary>
    /// <remarks>
    /// <para>A <em>suggestion</em>, with no authority of its own. Without it, consuming an addon whose
    /// package lives on a private feed means tracing that feed by hand — and again for everything the
    /// addon pulls in transitively, which is exactly the work a package manager should absorb. With
    /// it silently trusted, any third-party addon could add a source and shadow a public package name
    /// for the whole project.</para>
    /// <para>So the consuming project decides: a feed on a host in its <c>[nuget] trusted_hosts</c> is
    /// adopted automatically, anything else needs approval, and whatever is adopted is written into
    /// the project's own committed manifest where it shows up in review.</para>
    /// </remarks>
    public IReadOnlyList<NuGetSourceSpec> NuGetSources { get; init; } = [];
}
