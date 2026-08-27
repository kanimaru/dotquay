namespace Dotquay.Core.Manifest;

/// <summary>The parsed contents of an addon-side <c>dotquay.toml</c> (SPEC.md §7.2) — same schema
/// as <see cref="ProjectManifest"/> plus a <c>[package]</c> header. Ships inside the addon's own
/// folder next to <c>plugin.cfg</c>, so an addon repo is itself a valid dotquay project. Third-party
/// addons without this file are leaf nodes in the dependency graph (SPEC.md §8).</summary>
public sealed class PackageManifest
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    public IReadOnlyDictionary<string, AddonSpec> Addons { get; init; } = new Dictionary<string, AddonSpec>();

    public IReadOnlyDictionary<string, DotnetSpec> Dotnet { get; init; } = new Dictionary<string, DotnetSpec>();
}
