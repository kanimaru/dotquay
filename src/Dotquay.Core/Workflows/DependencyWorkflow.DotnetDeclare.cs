using Dotquay.Core.Dotnet;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Workflows;

/// <summary>
/// <c>dotquay add &lt;package-id&gt; [--version &lt;v&gt;]</c> for every state the project can be in
/// with respect to that package: unknown, locked but not declared, or declared (DESIGN-v0.19 §7a.1).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AddDotnetAsync"/> answers only the first of those and refuses the third, which left the
/// graph's package nodes with no way to say "use this exact version". This entry point dispatches
/// instead of widening <see cref="AddDotnetAsync"/>. That keeps the add path, with its source-generator
/// question and its "new packages only" contract, exactly as it was and as its tests pin it.
/// </para>
/// <para>
/// A pin or a change never re-decides what an earlier step already decided. A change keeps the
/// entry's feed pin and asset metadata. A pin takes the asset metadata dotquay already wrote into the
/// <c>.csproj</c> for the addon's declaration, as the lockfile recorded it. Only the version is a new
/// statement.
/// </para>
/// </remarks>
public sealed partial class DependencyWorkflow
{
    public async Task<DotnetDeclaration> DeclareDotnetAsync(
        string projectDirectory,
        string packageId,
        string? version = null,
        string? pinnedSourceName = null,
        CancellationToken cancellationToken = default)
    {
        var (manifest, manifestPath) = ReadManifestOrThrow(projectDirectory);
        var (lockfile, _) = ReadOrCreateLockfile(projectDirectory);
        var locked = lockfile.Dotnet.FirstOrDefault(d => d.Name == packageId);
        var declared = manifest.Dotnet.GetValueOrDefault(packageId);

        if (declared is null && locked is null)
        {
            var added = await AddDotnetAsync(projectDirectory, packageId, version, pinnedSourceName, cancellationToken);
            return new DotnetDeclaration(added, DotnetDeclarationOutcome.Added, PreviousResolved: null);
        }

        if (version is null && declared is not null)
        {
            throw new WorkflowException(
                $"'{packageId}' is already declared in {manifestPath}. Use 'dotquay update {packageId}' for the "
                + $"newest version its range allows, or 'dotquay add {packageId} --version <version>' to change it.");
        }

        if (declared is not null
            && locked is not null
            && declared.Version == version
            && locked.Resolved == version
            && (pinnedSourceName is null || pinnedSourceName == declared.Source))
        {
            return new DotnetDeclaration(locked, DotnetDeclarationOutcome.Unchanged, locked.Resolved);
        }

        var relocked = await RedeclareDotnetAsync(
            projectDirectory,
            packageId,
            version,
            // An explicit --source is a new statement; otherwise whatever the entry already pinned stands.
            pinnedSourceName ?? declared?.Source,
            // What the entry declared, or failing that what dotquay last wrote for the addon's declaration.
            declared?.Assets ?? locked?.Assets ?? PackageAssets.None,
            cancellationToken);

        // A range re-declared as it was, still resolving where it did, changed nothing. The early return
        // above can only see that for an exact pin, whose text is its own resolution.
        var outcome = declared is null
            ? DotnetDeclarationOutcome.Pinned
            : declared.Version == version && locked?.Resolved == relocked.Resolved && pinnedSourceName is null
                ? DotnetDeclarationOutcome.Unchanged
                : DotnetDeclarationOutcome.Changed;
        return new DotnetDeclaration(relocked, outcome, locked?.Resolved);
    }

    /// <summary>
    /// Resolves the highest version inside <paramref name="requestedVersion"/>, or the latest stable when
    /// it is null, then writes it into the <c>.csproj</c>, restores, and records it in both the manifest and
    /// the lockfile. The shared second half of a pin and a change.
    /// </summary>
    private async Task<LockedDotnet> RedeclareDotnetAsync(
        string projectDirectory,
        string packageId,
        string? requestedVersion,
        string? sourceName,
        PackageAssets assets,
        CancellationToken cancellationToken)
    {
        var (manifest, manifestPath) = ReadManifestOrThrow(projectDirectory);
        var sourceSet = manifest.SourceSet();
        ThrowIfUnknownSource(sourceSet, sourceName);

        var constraint = ParseDotnetConstraintOrThrow(packageId, requestedVersion);

        var nuGetClient = _nuGetClientFactory.Create(sourceSet, sourceName);
        var (version, resolvedSource) = await ResolveDotnetVersionOrThrowAsync(
            nuGetClient, packageId, constraint, cancellationToken);

        // Read before the install, for the same reason AddDotnetAsync does: the install rewrites
        // NuGet.Config's <packageSourceMapping> wholesale from what the lockfile knows.
        var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);
        var previous = lockfile.Dotnet.FirstOrDefault(d => d.Name == packageId);

        var locked = await InstallDotnetRestoringOnFailureAsync(
            projectDirectory,
            () => InstallDotnetOrThrowAsync(
                projectDirectory, packageId, version, manifest, lockfile.Dotnet, restore: true, resolvedSource,
                cancellationToken, assets, previouslyWritten: previous?.Assets));

        WriteManifest(manifestPath, manifest with
        {
            Dotnet = new Dictionary<string, DotnetSpec>(manifest.Dotnet)
            {
                [packageId] = new DotnetSpec
                {
                    Name = packageId,
                    Version = requestedVersion ?? CompatibleRange(version),
                    Source = sourceName,
                    Assets = assets,
                },
            },
        });

        // Replaced in place rather than appended, so a pin doesn't reorder the lockfile's [[dotnet]] list.
        var relocked = previous is not null
            ? lockfile.Dotnet.Select(d => d.Name == packageId ? locked : d).ToList()
            : lockfile.Dotnet.Append(locked).ToList();
        WriteLockfile(lockfilePath, lockfile with { Dotnet = relocked });

        return locked;
    }

    /// <summary>Refuses a feed name the manifest doesn't declare, before any network call, phrased for
    /// the <c>--source</c> flag the user actually typed.</summary>
    private static void ThrowIfUnknownSource(NuGetSourceSet sourceSet, string? sourceName)
    {
        if (sourceName is null || sourceSet.ByName(sourceName) is not null)
        {
            return;
        }

        var declared = sourceSet.All.Select(source => $"'{source.Name}'").ToList();
        throw new WorkflowException(
            $"--source '{sourceName}' isn't a declared NuGet feed. Declared: {string.Join(", ", declared)}. "
            + "Add it with 'dotquay source add <name> <url>'.");
    }
}
