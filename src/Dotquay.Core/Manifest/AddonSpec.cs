namespace Dotquay.Core.Manifest;

/// <summary>Which of the three mutually-exclusive source kinds an <see cref="AddonSpec"/> uses
/// (SPEC.md §7.1: "each [addons.*] entry names exactly one of version, branch, or rev").</summary>
public enum AddonSourceKind {
    Version,
    Branch,
    Rev,
}

/// <summary>One <c>[addons.&lt;name&gt;]</c> or <c>[overrides.&lt;name&gt;]</c> entry from a
/// <c>dotquay.toml</c> (SPEC.md §7.1).</summary>
public sealed record AddonSpec {
    public required string Name { get; init; }

    public required string Git { get; init; }

    /// <summary>Matched against git tags, e.g. <c>"~2.44"</c>.</summary>
    public string? Version { get; init; }

    /// <summary>Tracks a branch tip; the lockfile pins the resolved SHA.</summary>
    public string? Branch { get; init; }

    /// <summary>Hard pin to a commit, for repos that never tag releases.</summary>
    public string? Rev { get; init; }

    /// <summary>
    /// Subtree to extract from the addon's repo. Defaults to <c>addons/&lt;name&gt;</c>
    /// (<see cref="EffectivePath"/>). Repos where the addon lives at the repo root must set this
    /// to <c>"."</c> explicitly — dotquay never infers layout (decided, SPEC.md §7.1, OQ-2).
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Folder name to install into under <c>addons/</c>, overriding the default of the addon's own
    /// name (<see cref="EffectiveFolder"/>). The counterpart to <see cref="Path"/>: that one says
    /// where the addon lives in <em>its</em> repo, this one says where it lands in <em>your</em>
    /// project.
    /// </summary>
    /// <remarks>
    /// Exists because a Godot addon's folder name is frequently part of its public contract rather
    /// than a preference. Addons routinely <c>preload("res://addons/&lt;Name&gt;/...")</c>, which
    /// Godot resolves at parse time — install such an addon under any other name and its dependents
    /// fail to compile, not at runtime but on load. The canonical folder often differs from both the
    /// repo name and whatever key a manifest happens to use, so it needs to be stateable
    /// independently of the addon's identity (SPEC.md §7.1).
    /// </remarks>
    public string? InstallAs { get; init; }

    /// <summary>Exactly one of <see cref="Version"/>, <see cref="Branch"/>, or <see cref="Rev"/> is
    /// set by the time an <see cref="AddonSpec"/> exists — <see cref="Manifest.ManifestReader"/>
    /// enforces that invariant while parsing.</summary>
    public AddonSourceKind SourceKind =>
        Rev is not null ? AddonSourceKind.Rev :
        Branch is not null ? AddonSourceKind.Branch :
        AddonSourceKind.Version;

    public string EffectivePath => Path ?? $"addons/{Name}";

    /// <summary>The folder under <c>addons/</c> this addon installs into. Defaults to the addon's
    /// name, which is what every project relied on before <see cref="InstallAs"/> existed.</summary>
    public string EffectiveFolder => InstallAs ?? Name;
}
