namespace Dotquay.Core.Manifest;

/// <summary>The parsed contents of a project's <c>dotquay.toml</c> (SPEC.md §7.1) — "what you
/// want", the input to the resolver.</summary>
public sealed record ProjectManifest
{
    // There is deliberately no `godot` property. [project] carried a `godot` key through v0.5 that
    // nothing ever read — it was parsed, stored, and written back out, and the scaffold hardcoded
    // "4.3" regardless of the project it was initialising, so it mostly served to state the wrong
    // version confidently. A real Godot-compatibility check needs the version from project.godot and
    // a declared range from each addon; until that exists, an unused field that drifts is worse than
    // no field. ManifestReader still tolerates the key so existing manifests keep parsing.

    /// <summary>V3 service-index URL of the NuGet source to query for <c>[dotnet]</c> dependencies —
    /// e.g. a private GitLab/Azure Artifacts/MyGet feed. Null means the public default
    /// (nuget.org). Never a place for credentials: those come from the
    /// <c>DOTQUAY_NUGET_USERNAME</c>/<c>DOTQUAY_NUGET_TOKEN</c> environment variables specifically
    /// so a committed <c>dotquay.toml</c> never carries a secret (SPEC.md §11).</summary>
    public string? NuGetSource { get; init; }

    /// <summary>
    /// Where captured patches are written and read from (SPEC.md §10), relative to the project.
    /// Null means the default — <c>.dotquay/patches</c>, or an existing <c>patches/</c> folder if the
    /// project predates v0.6.
    /// </summary>
    /// <remarks>
    /// Configurable because the folder is committed source that lives in everyone's repository root,
    /// and projects reasonably disagree about where such a thing belongs. Whatever it is set to must
    /// stay tracked by git: patches only work if teammates get them.
    /// </remarks>
    public string? PatchesDirectory { get; init; }

    public IReadOnlyDictionary<string, AddonSpec> Addons { get; init; } = new Dictionary<string, AddonSpec>();

    public IReadOnlyDictionary<string, DotnetSpec> Dotnet { get; init; } = new Dictionary<string, DotnetSpec>();

    /// <summary>Project-level overrides always win resolution conflicts — the escape hatch
    /// (SPEC.md §8).</summary>
    public IReadOnlyDictionary<string, AddonSpec> Overrides { get; init; } = new Dictionary<string, AddonSpec>();
}
