using Dotquay.Core.Abstractions;
using Dotquay.Core.Auth;
using Dotquay.Core.Diagnostics;
using Dotquay.Core.Dotnet.SourceBuild;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Dotnet;

/// <summary>
/// Syncs one resolved NuGet dependency into the project: writes (or updates) its
/// <c>PackageReference</c> in the <c>.csproj</c>, then shells out to <c>dotnet restore</c> (SPEC.md
/// §11, requirement P0-7). The dotnet-side analog of <see cref="Sync.AddonInstaller"/> — same "one
/// piece <c>add</c>/<c>install</c>/<c>update</c> share" role, for the NuGet half of the pipeline.
/// </summary>
/// <remarks>
/// When the manifest declares private feeds, this is also the piece that makes <c>dotnet
/// restore</c> — a separate process with its own config, unaware of dotquay's own
/// <see cref="NuGetV3Client"/> queries — able to reach them: it ensures <c>NuGet.Config</c>
/// declares every source (see <see cref="NuGetConfigWriter"/>, no credentials, safe to commit) and
/// passes one <c>NuGetPackageSourceCredentials_&lt;key&gt;</c> variable per authenticated source on
/// the restore child process itself, resolved through the same <see cref="CredentialResolver"/> the
/// read side uses — one rule, two consumers, never written to disk.
/// </remarks>
public sealed class DotnetInstaller {
    private readonly IFileSystem _fileSystem;
    private readonly IDotnetCliRunner _cliRunner;
    private readonly CredentialResolver _credentials;

    public DotnetInstaller(IFileSystem fileSystem, IDotnetCliRunner cliRunner,
        CredentialResolver credentials) {
        _fileSystem = fileSystem;
        _cliRunner = cliRunner;
        _credentials = credentials;
    }

    /// <summary>Writes the PackageReference and restores. <paramref name="restore"/> defaults to
    /// true; batch callers (installing several dotnet deps at once) can pass false and restore once
    /// at the end instead of once per package. <paramref name="sources"/> is the project's feed
    /// set, passed through so NuGet.Config and the restore process's credentials stay in sync with
    /// wherever the package was resolved from. <paramref name="resolvedSourceName"/> is the feed
    /// that actually answered, recorded in the lockfile.</summary>
    public async Task<LockedDotnet> InstallAsync(
        string projectDirectory,
        string packageId,
        string version,
        NuGetSourceSet? sources = null,
        bool restore = true,
        string? resolvedSourceName = null,
        IReadOnlyDictionary<string, string>? pinnedPackages = null,
        bool writeSourceMapping = true,
        Manifest.PackageAssets? assets = null,
        Manifest.PackageAssets? previouslyWritten = null,
        CancellationToken cancellationToken = default) =>
        (await InstallWithOutcomeAsync(
            projectDirectory, packageId, version, sources, restore, resolvedSourceName,
            pinnedPackages,
            writeSourceMapping, assets, previouslyWritten, cancellationToken)).Locked;

    /// <summary>
    /// <see cref="InstallAsync"/>, also saying whether the <c>.csproj</c> was left without a
    /// reference because the project builds the package from source
    /// (docs/DESIGN-v0.26-SOURCE-BUILT-PACKAGES.md).
    /// </summary>
    /// <remarks>
    /// The check lives here, not in the workflow, because this is the one place a
    /// <c>PackageReference</c> is written: <c>install</c>, <c>update</c>, <c>add</c> and the graph
    /// sync all arrive through it, so none of them can forget. Everything else this method does -
    /// feeds, restore, the lockfile entry - is the same either way: the lockfile still records the
    /// published version, so the entry stays meaningful on a machine or branch without the source
    /// reference.
    /// </remarks>
    public async Task<DotnetInstallOutcome> InstallWithOutcomeAsync(
        string projectDirectory,
        string packageId,
        string version,
        NuGetSourceSet? sources = null,
        bool restore = true,
        string? resolvedSourceName = null,
        IReadOnlyDictionary<string, string>? pinnedPackages = null,
        bool writeSourceMapping = true,
        Manifest.PackageAssets? assets = null,
        Manifest.PackageAssets? previouslyWritten = null,
        CancellationToken cancellationToken = default) {
        var csprojPath = CsprojLocator.Locate(_fileSystem, projectDirectory);
        var sourceBuilt = ReferenceUnlessBuiltFromSource(csprojPath, packageId, version, assets,
            previouslyWritten);

        var effectiveSources = sources ?? NuGetSourceSet.PublicOnly;

        if (effectiveSources.HasCustomSources) {
            NuGetConfigWriter.EnsureSources(
                _fileSystem, projectDirectory, effectiveSources, pinnedPackages,
                writeSourceMapping);
        }

        if (restore) {
            await _cliRunner.RestoreAsync(csprojPath, BuildCredentialEnvironment(effectiveSources),
                cancellationToken);
        }

        // Records what was written, not what was asked for — they are the same here, but the
        // lockfile's job is to be the record of the action, so the next sync can remove metadata a
        // manifest has since withdrawn.
        var locked = new LockedDotnet {
            Name = packageId,
            Resolved = version,
            Source = resolvedSourceName,
            Assets = assets ?? Manifest.PackageAssets.None,
        };

        return new DotnetInstallOutcome(locked, sourceBuilt);
    }

    /// <summary>Writes the package's <c>PackageReference</c>, unless an unconditional
    /// <c>ProjectReference</c> already builds it - then makes sure there is none, and says
    /// so.</summary>
    private SourceBuiltPackage? ReferenceUnlessBuiltFromSource(
        string csprojPath,
        string packageId,
        string version,
        Manifest.PackageAssets? assets,
        Manifest.PackageAssets? previouslyWritten) {
        var provider = SourceProvidedPackages.FindProvider(_fileSystem, csprojPath, packageId);

        if (provider is null) {
            CsprojPackageReferenceWriter.AddOrUpdate(_fileSystem, csprojPath, packageId, version,
                assets, previouslyWritten);
            return null;
        }

        // Decision 1 (2026-11-03): beside an unconditional source build a PackageReference is never
        // valid - MSBuild picks one of the two and the project cannot tell which - so it goes,
        // whoever wrote it.
        var removed = CsprojPackageReferenceWriter.Remove(_fileSystem, csprojPath, packageId);

        DiagnosticLog.Write(
            DiagnosticLevel.Verbose,
            "dotnet",
            $"{packageId}: built from source by {provider.Include} ({provider.ProjectPath}); "
                + (removed ? "removed its PackageReference" : "no PackageReference written"));

        return new SourceBuiltPackage(packageId, provider, removed);
    }

    /// <summary>Removes the PackageReference if present. Does not restore — an obj/ cache one
    /// version behind what the .csproj declares is harmless until the next real build, unlike
    /// installing (SPEC.md §9's "never leave addons/ half-updated" doesn't have a dotnet-side
    /// equivalent risk here).</summary>
    public bool Remove(string projectDirectory, string packageId) {
        var csprojPath = CsprojLocator.Locate(_fileSystem, projectDirectory);
        return CsprojPackageReferenceWriter.Remove(_fileSystem, csprojPath, packageId);
    }

    /// <summary>
    /// Restores, first rewriting <c>NuGet.Config</c> from the complete picture when one is given.
    /// </summary>
    /// <remarks>
    /// <paramref name="mappedPackages"/> is written here rather than only inside
    /// <see cref="InstallAsync"/> because install runs once per package and therefore only ever
    /// knows a partial mapping — and a package installed early would end up bound by a mapping
    /// written before the later ones were resolved. The final restore is the one moment the whole
    /// set is known.
    /// </remarks>
    public Task RestoreAsync(
        string projectDirectory,
        NuGetSourceSet? sources = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? mappedPackages = null,
        bool writeSourceMapping = true) {
        var effectiveSources = sources ?? NuGetSourceSet.PublicOnly;

        if (mappedPackages is not null && effectiveSources.HasCustomSources) {
            NuGetConfigWriter.EnsureSources(
                _fileSystem, projectDirectory, effectiveSources, mappedPackages,
                writeSourceMapping);
        }

        return _cliRunner.RestoreAsync(
            CsprojLocator.Locate(_fileSystem, projectDirectory),
            BuildCredentialEnvironment(effectiveSources),
            cancellationToken);
    }

    /// <summary>
    /// One <c>NuGetPackageSourceCredentials_&lt;key&gt;</c> variable per authenticated source.
    /// </summary>
    /// <remarks>
    /// Null when nothing is authenticated, so a plain public-only project's restore child process
    /// is untouched. nuget.org contributes nothing regardless — there is no credential for it, and
    /// sending a private feed's token to a public host would leak it.
    /// </remarks>
    private IReadOnlyDictionary<string,
        string>? BuildCredentialEnvironment(NuGetSourceSet sources) {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var source in sources.All.Where(source => !source.IsPublicDefault)) {
            var lookup = _credentials.ForNuGetSource(source.Name, source.Url);
            if (lookup.Credential is null) {
                continue;
            }

            variables[$"NuGetPackageSourceCredentials_{source.ConfigKey}"] =
                $"Username={lookup.Credential.Username};Password={lookup.Credential.Token}";
        }

        return variables.Count == 0 ? null : variables;
    }
}
