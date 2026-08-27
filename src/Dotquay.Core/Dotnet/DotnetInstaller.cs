using Dotquay.Core.Abstractions;
using Dotquay.Core.Lockfile;

namespace Dotquay.Core.Dotnet;

/// <summary>
/// Syncs one resolved NuGet dependency into the project: writes (or updates) its
/// <c>PackageReference</c> in the <c>.csproj</c>, then shells out to <c>dotnet restore</c> (SPEC.md
/// §11, requirement P0-7). The dotnet-side analog of <see cref="Sync.AddonInstaller"/> — same "one
/// piece <c>add</c>/<c>install</c>/<c>update</c> share" role, for the NuGet half of the pipeline.
/// </summary>
/// <remarks>
/// When the manifest declares a private <c>nuget_source</c>, this is also the piece that makes
/// <c>dotnet restore</c> — a separate process with its own config, unaware of dotquay's own
/// <see cref="NuGetV3Client"/> queries — actually able to reach it: it ensures <c>NuGet.Config</c>
/// declares the source (see <see cref="NuGetConfigWriter"/>, no credentials, safe to commit) and
/// passes <c>NuGetPackageSourceCredentials_dotquay</c> as an environment variable on the restore
/// child process itself, translated from the same <c>DOTQUAY_NUGET_USERNAME</c>/
/// <c>DOTQUAY_NUGET_TOKEN</c> variables <see cref="NuGetV3ClientFactory"/> reads (via
/// <see cref="NuGetCredentials"/>) — one convention, two consumers, never written to disk.
/// </remarks>
public sealed class DotnetInstaller
{
    private readonly IFileSystem _fileSystem;
    private readonly IDotnetCliRunner _cliRunner;

    public DotnetInstaller(IFileSystem fileSystem, IDotnetCliRunner cliRunner)
    {
        _fileSystem = fileSystem;
        _cliRunner = cliRunner;
    }

    /// <summary>Writes the PackageReference and restores. <paramref name="restore"/> defaults to
    /// true; batch callers (installing several dotnet deps at once) can pass false and restore once
    /// at the end instead of once per package. <paramref name="nuGetSourceUrl"/> is the manifest's
    /// <c>[project] nuget_source</c> (null for the public default) — passed through so NuGet.Config
    /// and the restore process's credentials stay in sync with wherever the package was resolved
    /// from.</summary>
    public async Task<LockedDotnet> InstallAsync(
        string projectDirectory, string packageId, string version, string? nuGetSourceUrl = null,
        bool restore = true, CancellationToken cancellationToken = default)
    {
        var csprojPath = CsprojLocator.Locate(_fileSystem, projectDirectory);
        CsprojPackageReferenceWriter.AddOrUpdate(_fileSystem, csprojPath, packageId, version);

        if (nuGetSourceUrl is not null)
        {
            NuGetConfigWriter.EnsureSource(_fileSystem, projectDirectory, nuGetSourceUrl);
        }

        if (restore)
        {
            await _cliRunner.RestoreAsync(csprojPath, BuildCredentialEnvironment(nuGetSourceUrl), cancellationToken);
        }

        return new LockedDotnet { Name = packageId, Resolved = version };
    }

    /// <summary>Removes the PackageReference if present. Does not restore — an obj/ cache one
    /// version behind what the .csproj declares is harmless until the next real build, unlike
    /// installing (SPEC.md §9's "never leave addons/ half-updated" doesn't have a dotnet-side
    /// equivalent risk here).</summary>
    public bool Remove(string projectDirectory, string packageId)
    {
        var csprojPath = CsprojLocator.Locate(_fileSystem, projectDirectory);
        return CsprojPackageReferenceWriter.Remove(_fileSystem, csprojPath, packageId);
    }

    public Task RestoreAsync(string projectDirectory, string? nuGetSourceUrl = null, CancellationToken cancellationToken = default) =>
        _cliRunner.RestoreAsync(
            CsprojLocator.Locate(_fileSystem, projectDirectory), BuildCredentialEnvironment(nuGetSourceUrl), cancellationToken);

    /// <summary>Builds the one env var <c>dotnet restore</c> needs to authenticate against the
    /// <see cref="NuGetConfigWriter.DotquaySourceName"/> source — absent entirely for the public
    /// default source or when no credentials are configured, so a plain public-only project's
    /// restore child process is untouched.</summary>
    private static IReadOnlyDictionary<string, string>? BuildCredentialEnvironment(string? nuGetSourceUrl)
    {
        if (nuGetSourceUrl is null)
        {
            return null;
        }

        var (username, token) = NuGetCredentials.FromEnvironment();
        if (username is null || token is null)
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            [$"NuGetPackageSourceCredentials_{NuGetConfigWriter.DotquaySourceName}"] = $"Username={username};Password={token}",
        };
    }
}
