using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Dotnet;

/// <summary>
/// Bridges a <see cref="ProjectManifest"/> to the NuGet side: its feed set, and the package-to-feed
/// pins that drive <c>&lt;packageSourceMapping&gt;</c>.
/// </summary>
/// <remarks>
/// Extension methods in the <c>Dotnet</c> namespace rather than properties on
/// <see cref="ProjectManifest"/>, so the manifest model stays plain data with no knowledge of how
/// feeds are searched — the same separation that keeps <c>ManifestReader</c> the only thing that
/// understands TOML.
/// </remarks>
public static class ProjectNuGetSources {
    /// <summary>The project's feeds in declared order, with nuget.org appended.</summary>
    public static NuGetSourceSet SourceSet(this ProjectManifest manifest) =>
        NuGetSourceSet.From(manifest.NuGetSources);

    /// <summary>Package ID to pinned source name, for every <c>[dotnet.*]</c> that named
    /// one.</summary>
    public static IReadOnlyDictionary<string,
        string> PinnedPackages(this ProjectManifest manifest) =>
        manifest.Dotnet.Values
            .Where(spec => spec.Source is not null)
            .ToDictionary(spec => spec.Name, spec => spec.Source!,
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Everything <c>&lt;packageSourceMapping&gt;</c> should bind: the project's explicit pins,
    /// every package whose resolved feed the lockfile records, and each source's owned ID patterns.
    /// </summary>
    /// <remarks>
    /// <para>Including the lockfile's resolved feeds is a tightening that stands on its own:
    /// previously only hand-written pins were mapped, so <c>dotnet restore</c> was free to fetch an
    /// unpinned package from a different feed than the one dotquay resolved it from. Now restore is
    /// bound to what resolution actually chose.</para>
    /// <para>Owned patterns matter for adopted feeds specifically: a private package's own private
    /// transitive dependencies appear in no manifest and no lockfile, so without a
    /// <c>VBeami.*</c>-style pattern they would match nothing, fall to the <c>*</c> catch-all, and
    /// be looked for on nuget.org.</para>
    /// </remarks>
    /// <exception cref="DotnetSyncException">An adopted feed claims a package ID the project
    /// already declares, or that resolved from a different source. That is the case where an addon
    /// tries to take over a well-known package, and guessing which claimant wins is not dotquay's
    /// call.</exception>
    public static IReadOnlyDictionary<string, string> MappedPackages(
        this ProjectManifest manifest, IReadOnlyList<LockedDotnet> lockedDotnet) {
        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var locked in lockedDotnet.Where(d => d.Source is not null)) {
            mapped[locked.Name] = locked.Source!;
        }

        // Explicit project pins are applied last so they win over whatever the lockfile recorded —
        // a pin is a statement of intent, and the lockfile is a record of a past resolution.
        foreach (var (packageId, sourceName) in manifest.PinnedPackages()) {
            mapped[packageId] = sourceName;
        }

        foreach (var source in manifest.NuGetSources.Where(s => s.Packages.Count > 0)) {
            foreach (var pattern in source.Packages) {
                if (source.DeclaredByAddon is not null
                    && mapped.TryGetValue(pattern, out var existing)
                    && !string.Equals(existing, source.Name, StringComparison.Ordinal)) {
                    throw new DotnetSyncException(
                        $"NuGet source '{source.Name}' (adopted from addon "
                        + $"'{source.DeclaredByAddon}') claims package '{pattern}', which already "
                        + $"resolves from '{existing}'. Refusing to let an "
                        + "adopted feed take over a package the project already gets elsewhere — "
                        + $"remove the [nuget.sources.{source.Name}] block, or pin '{pattern}' "
                        + $"explicitly to settle it.");
                }

                mapped[pattern] = source.Name;
            }
        }

        return mapped;
    }
}
