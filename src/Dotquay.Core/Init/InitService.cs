using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Init;

/// <summary>Implements <c>dotquay init</c> and <c>dotquay init --package</c> (SPEC.md §6): scaffold
/// a manifest and never overwrite one that already exists — <c>init</c> is meant to be safe to run
/// by habit, the way <c>git init</c> is.</summary>
public sealed class InitService {
    private readonly IFileSystem _fileSystem;

    public InitService(IFileSystem fileSystem) {
        _fileSystem = fileSystem;
    }

    /// <summary>Scaffolds a project manifest (SPEC.md §7.1) in <paramref name="projectDirectory"/>.
    /// <paramref name="nuGetSource"/> records a private NuGet feed's V3 service-index URL, if the
    /// project uses one — see <see cref="Manifest.ProjectManifest.NuGetSource"/>.</summary>
    public InitResult InitProject(string projectDirectory, string? nuGetSource = null) {
        var manifestPath = _fileSystem.CombinePath(projectDirectory, "dotquay.toml");

        if (_fileSystem.FileExists(manifestPath)) {
            return InitResult.AlreadyExists(manifestPath);
        }

        _fileSystem.CreateDirectory(projectDirectory);
        _fileSystem.WriteAllText(manifestPath, ManifestWriter.ScaffoldProjectToml(nuGetSource));

        return InitResult.Created(manifestPath);
    }

    /// <summary>Scaffolds a package manifest (SPEC.md §7.2) inside an addon folder.</summary>
    public InitResult InitPackage(string addonDirectory, string packageName) {
        var manifestPath = _fileSystem.CombinePath(addonDirectory, "dotquay.toml");

        if (_fileSystem.FileExists(manifestPath)) {
            return InitResult.AlreadyExists(manifestPath);
        }

        _fileSystem.CreateDirectory(addonDirectory);
        _fileSystem.WriteAllText(manifestPath, ManifestWriter.ScaffoldPackageToml(packageName));

        return InitResult.Created(manifestPath);
    }
}
