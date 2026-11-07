using Dotquay.Core.Manifest;

namespace Dotquay.Core.Dotnet;

/// <summary>
/// The feeds a project searches, in order, with the public default appended (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// <para>A list, not a dictionary, because the order <em>is</em> the resolution rule and
/// <c>Dictionary&lt;,&gt;</c> does not guarantee enumeration order by contract. Depending on its
/// implementation detail here would make dependency resolution quietly non-deterministic.</para>
/// <para>Unpinned packages take the first feed that has them rather than the highest version across
/// all of them. Taking the highest is a dependency-confusion substitution attack with extra steps:
/// a public package can outrank the private one whose name it shadows.</para>
/// </remarks>
public sealed class NuGetSourceSet {
    private readonly List<NuGetSourceSpec> _sources;
    private readonly List<NuGetSourceSpec> _declared;

    private NuGetSourceSet(List<NuGetSourceSpec> sources, List<NuGetSourceSpec> declared) {
        _sources = sources;
        _declared = declared;
    }

    /// <summary>Declared sources in manifest order, then <c>nuget.org</c> — unless a declared
    /// source already points there, in which case it keeps its declared position and no duplicate
    /// is appended.</summary>
    public static NuGetSourceSet From(IReadOnlyList<NuGetSourceSpec> declared) {
        var sources = declared.ToList();

        if (!sources.Any(source => source.IsPublicDefault)) {
            sources.Add(NuGetSourceSpec.Public);
        }

        return new NuGetSourceSet(sources, declared.ToList());
    }

    public static NuGetSourceSet PublicOnly { get; } = From([]);

    /// <summary>Every source, in search order.</summary>
    public IReadOnlyList<NuGetSourceSpec> All => _sources;

    /// <summary>
    /// Only what the manifest actually declared — never the implicitly appended <c>nuget.org</c>.
    /// </summary>
    /// <remarks>
    /// Tracked separately rather than derived by filtering out the public URL, because a project
    /// may legitimately declare nuget.org itself (to control its position in the search order), and
    /// that declaration must survive a manifest rewrite. Filtering would silently delete it.
    /// </remarks>
    public IReadOnlyList<NuGetSourceSpec> Declared => _declared;

    public bool HasCustomSources => _sources.Any(source => !source.IsPublicDefault);

    /// <summary>
    /// Narrows the set to a single source when a package pins one with <c>source =
    /// "&lt;name&gt;"</c>.
    /// </summary>
    /// <exception cref="ManifestParseException">The pin names a source that isn't declared. Listing
    /// the ones that are turns "typo" from a mystery into a correction.</exception>
    public NuGetSourceSet Resolve(string? pinnedSourceName) {
        if (pinnedSourceName is null) {
            return this;
        }

        var match = _sources.FirstOrDefault(
            source => string.Equals(source.Name, pinnedSourceName, StringComparison.Ordinal));

        if (match is null) {
            var known = string.Join(", ", _sources.Select(source => $"'{source.Name}'"));
            throw new ManifestParseException(
                $"source = \"{pinnedSourceName}\" doesn't match any declared NuGet source. "
                + $"Declared: {known}.");
        }

        return new NuGetSourceSet([match], [match]);
    }

    public NuGetSourceSpec? ByName(string name) =>
        _sources.FirstOrDefault(source =>
            string.Equals(source.Name, name, StringComparison.Ordinal));
}
