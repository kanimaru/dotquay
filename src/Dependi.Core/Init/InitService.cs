using Dependi.Core.Abstractions;
using Dependi.Core.Manifest;

namespace Dependi.Core.Init;

/// <summary>Implements <c>dependi init</c> and <c>dependi init --package</c> (SPEC.md §6): scaffold
/// a manifest and never overwrite one that already exists — <c>init</c> is meant to be safe to run
/// by habit, the way <c>git init</c> is.</summary>
public sealed class InitService
{
    private readonly IFileSystem _fileSystem;

    public InitService(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>Scaffolds a project manifest (SPEC.md §7.1) in <paramref name="projectDirectory"/>.
    /// <paramref name="nuGetSource"/> records a private NuGet feed's V3 service-index URL, if the
    /// project uses one — see <see cref="Manifest.ProjectManifest.NuGetSource"/>.</summary>
    public InitResult InitProject(string projectDirectory, string? godotVersion = null, string? nuGetSource = null)
    {
        var manifestPath = _fileSystem.CombinePath(projectDirectory, "dependi.toml");

        if (_fileSystem.FileExists(manifestPath))
        {
            return InitResult.AlreadyExists(manifestPath);
        }

        _fileSystem.CreateDirectory(projectDirectory);
        _fileSystem.WriteAllText(manifestPath, ManifestWriter.ScaffoldProjectToml(godotVersion, nuGetSource));

        return InitResult.Created(manifestPath);
    }

    /// <summary>Scaffolds a package manifest (SPEC.md §7.2) inside an addon folder.</summary>
    public InitResult InitPackage(string addonDirectory, string packageName, string packageVersion)
    {
        var manifestPath = _fileSystem.CombinePath(addonDirectory, "dependi.toml");

        if (_fileSystem.FileExists(manifestPath))
        {
            return InitResult.AlreadyExists(manifestPath);
        }

        _fileSystem.CreateDirectory(addonDirectory);
        _fileSystem.WriteAllText(manifestPath, ManifestWriter.ScaffoldPackageToml(packageName, packageVersion));

        return InitResult.Created(manifestPath);
    }
}
