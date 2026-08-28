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

    /// <summary>
    /// NuGet feeds to search for <c>[dotnet]</c> dependencies, in declared order — private GitLab,
    /// Azure Artifacts, MyGet, or nothing at all for a public-only project (SPEC.md §7.1, §11.1).
    /// </summary>
    /// <remarks>
    /// <para>A list, and ordered, because the order is the resolution rule: an unpinned package takes
    /// the first feed that has it (SPEC.md §11.1). Replaced the single <c>[project] nuget_source</c>
    /// string in v0.7, which couldn't describe a project consuming packages from two GitLab
    /// repositories — GitLab publishes one feed per project. The legacy key still parses and appears
    /// here as a source named <see cref="NuGetSourceSpec.LegacyName"/>.</para>
    /// <para>Never a place for credentials. Those come from <c>dotquay login</c> or the
    /// <c>DOTQUAY_NUGET_*</c> environment variables specifically so a committed <c>dotquay.toml</c>
    /// never carries a secret (SPEC.md §11.2), which <c>SecretLeakTests</c> asserts rather than
    /// assumes.</para>
    /// </remarks>
    public IReadOnlyList<NuGetSourceSpec> NuGetSources { get; init; } = [];

    /// <summary>
    /// Whether dotquay writes <c>&lt;packageSourceMapping&gt;</c> into the project's
    /// <c>NuGet.Config</c> (<c>[nuget] source_mapping</c>, default true).
    /// </summary>
    /// <remarks>
    /// On by default because without it multi-feed support is a report rather than a guarantee:
    /// <c>dotnet restore</c> is a separate process that searches every configured source on its own
    /// terms, so dotquay could resolve a package from a private feed while restore quietly took a
    /// same-named package from nuget.org. The opt-out exists for projects running their own mapping
    /// strategy, which dotquay must not fight.
    /// </remarks>
    public bool NuGetSourceMapping { get; init; } = true;

    /// <summary>
    /// Hosts whose addon-declared NuGet feeds are adopted without asking
    /// (<c>[nuget] trusted_hosts</c>, SPEC.md §11.3).
    /// </summary>
    /// <remarks>
    /// <para>The security control that makes addon-declared feeds usable rather than merely possible.
    /// Trusting a host here is a smaller step than it looks: the project already runs <em>code</em>
    /// from addons on that host, which is strictly more dangerous than reading packages from its
    /// feed. What it must not become is a wildcard — matching is exact host (and port), with no
    /// subdomain globbing, because <c>*.example.com</c> quietly matching an attacker-controlled
    /// <c>evil.example.com</c> is a trap that reads as safe.</para>
    /// <para>Empty by default: a project that never opts in behaves exactly as it did in v0.7, where
    /// only the project could declare a feed.</para>
    /// </remarks>
    public IReadOnlyList<string> TrustedHosts { get; init; } = [];

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
