namespace Dotquay.Core.Dotnet.SourceBuild;

/// <summary>
/// A package the installer did not reference because the project builds it from source
/// (docs/DESIGN-v0.26-SOURCE-BUILT-PACKAGES.md §B).
/// </summary>
/// <param name="RemovedPackageReference">Whether a <c>PackageReference</c> to it was in the
/// <c>.csproj</c> and has now been removed (decision 1, 2026-11-03).</param>
public sealed record SourceBuiltPackage(string PackageId, SourceReference Provider,
    bool RemovedPackageReference) {
    /// <summary>The one line every surface shows (CLI, <c>--json</c> <c>messages</c>, MCP), so all
    /// of them say the same thing about the same decision.</summary>
    /// <remarks>Deliberately no version comparison here: that is <c>doctor</c>'s job (decision 2),
    /// because <c>update</c> reaches the installer only on a change and a warning here would come
    /// and go.</remarks>
    public string Describe() {
        var version = Provider.Identity.Version is { } v ? $" ({v})" : string.Empty;
        var head = $"{PackageId}: built from source by {Provider.Include}{version}";

        return RemovedPackageReference
            ? $"{head}, so its PackageReference was removed from the .csproj - it could shadow the "
            + $"source build."
            : $"{head}, so no PackageReference was written.";
    }
}

/// <summary>What <see cref="DotnetInstaller.InstallWithOutcomeAsync"/> did: the lockfile entry, and
/// - when the package is built from source - why the <c>.csproj</c> was not given a
/// reference.</summary>
public sealed record DotnetInstallOutcome(Lockfile.LockedDotnet Locked,
    SourceBuiltPackage? SourceBuilt);
