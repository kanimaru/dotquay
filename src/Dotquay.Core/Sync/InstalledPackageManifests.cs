using Dotquay.Core.Abstractions;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Workflows;

namespace Dotquay.Core.Sync;

/// <summary>
/// Reads the package manifest (<c>dotquay.toml</c> with a <c>[package]</c> header, SPEC.md §7.2)
/// that an <em>installed</em> addon ships, straight off disk.
/// </summary>
/// <remarks>
/// <para>
/// The installed folder <em>is</em> the extracted subtree (SPEC.md §7.2), so this is exactly the
/// document the resolver read when the addon was resolved. That is what makes "which addon declared
/// this NuGet package?" answerable offline, with no network and no re-walk.
/// </para>
/// <para>
/// Before this type existed, the read lived as a private method on <see cref="DependencyWorkflow"/>,
/// and the graph view had no access to it at all. It passed <c>addonDeclared: []</c> and so couldn't
/// tell an addon-declared package from a leftover (DESIGN-v0.19). Doctor, the graph, and
/// <c>remove</c> now share one reader, for the same reason <see cref="InstallPaths"/> exists: several
/// copies of "where does an addon's manifest live" drift apart.
/// </para>
/// <para>
/// A manifest that exists but doesn't parse reads as "declares nothing". Reporting it is doctor's
/// <c>package_manifest_unreadable</c> check's job, once. Swallowing it here keeps every other caller
/// from reporting it a second time or failing a read-only view over it.
/// </para>
/// </remarks>
public sealed class InstalledPackageManifests
{
    private readonly IFileSystem _fileSystem;

    public InstalledPackageManifests(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>The package manifest installed under <c>addons/<paramref name="folder"/></c>, or
    /// null if there is none or it doesn't parse.</summary>
    public PackageManifest? TryRead(string projectDirectory, string folder)
    {
        var manifestPath = _fileSystem.CombinePath(
            InstallPaths.ForFolder(_fileSystem, projectDirectory, folder), ProjectFileStore.ManifestFileName);
        if (!_fileSystem.FileExists(manifestPath))
        {
            return null;
        }

        try
        {
            return ManifestReader.ReadPackage(_fileSystem.ReadAllText(manifestPath), manifestPath);
        }
        catch (ManifestParseException)
        {
            return null;
        }
    }

    /// <summary>
    /// Addon name to the <c>[dotnet.*]</c> entries its installed package manifest declares, for each
    /// of <paramref name="addons"/> that declares at least one.
    /// </summary>
    /// <remarks>
    /// Located through <see cref="LockedAddon.EffectiveFolder"/>, never <c>addons/&lt;name&gt;</c>. An
    /// addon installed under <c>install_as</c> keeps its manifest in the renamed folder, and a
    /// name-derived path would silently find nothing there. The caller decides which addons count
    /// (usually only the reachable ones); this type doesn't know about the graph.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<DotnetSpec>> DotnetDeclarationsByAddon(
        string projectDirectory, IEnumerable<LockedAddon> addons)
    {
        var result = new Dictionary<string, IReadOnlyList<DotnetSpec>>(StringComparer.Ordinal);

        foreach (var locked in addons)
        {
            var package = TryRead(projectDirectory, locked.EffectiveFolder);
            if (package is { Dotnet.Count: > 0 })
            {
                result[locked.Name] = package.Dotnet.Values.ToList();
            }
        }

        return result;
    }
}
