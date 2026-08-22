namespace Dependi.Core.Manifest;

/// <summary>The parsed contents of a project's <c>dependi.toml</c> (SPEC.md §7.1) — "what you
/// want", the input to the resolver.</summary>
public sealed record ProjectManifest
{
    /// <summary>Informational in v1 (SPEC.md §6); not used for branch auto-selection.</summary>
    public string? Godot { get; init; }

    /// <summary>V3 service-index URL of the NuGet source to query for <c>[dotnet]</c> dependencies —
    /// e.g. a private GitLab/Azure Artifacts/MyGet feed. Null means the public default
    /// (nuget.org). Never a place for credentials: those come from the
    /// <c>DEPENDI_NUGET_USERNAME</c>/<c>DEPENDI_NUGET_TOKEN</c> environment variables specifically
    /// so a committed <c>dependi.toml</c> never carries a secret (SPEC.md §11).</summary>
    public string? NuGetSource { get; init; }

    public IReadOnlyDictionary<string, AddonSpec> Addons { get; init; } = new Dictionary<string, AddonSpec>();

    public IReadOnlyDictionary<string, DotnetSpec> Dotnet { get; init; } = new Dictionary<string, DotnetSpec>();

    /// <summary>Project-level overrides always win resolution conflicts — the escape hatch
    /// (SPEC.md §8).</summary>
    public IReadOnlyDictionary<string, AddonSpec> Overrides { get; init; } = new Dictionary<string, AddonSpec>();
}
