using Dotquay.Core.Abstractions;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Workflows;

/// <summary>
/// The single place that knows where a project's <c>dotquay.toml</c> and <c>dotquay.lock</c> live
/// and how to read or write them, with the error messages users see when one is missing. Extracted
/// from <see cref="DependencyWorkflow"/>'s private helpers in v0.5, when
/// <see cref="Graph.GraphInspector"/> became a second consumer of exactly the same five operations —
/// two copies of "which file, what message, read or create" is precisely the kind of drift that ends
/// with the CLI and the editor plugin disagreeing about whether a project is initialised.
/// </summary>
public sealed class ProjectFileStore {
    public const string ManifestFileName = "dotquay.toml";
    public const string LockfileFileName = "dotquay.lock";

    private readonly IFileSystem _fileSystem;

    public ProjectFileStore(IFileSystem fileSystem) {
        _fileSystem = fileSystem;
    }

    public string ManifestPath(string projectDirectory) => _fileSystem.CombinePath(projectDirectory, ManifestFileName);

    public string LockfilePath(string projectDirectory) => _fileSystem.CombinePath(projectDirectory, LockfileFileName);

    public bool HasManifest(string projectDirectory) => _fileSystem.FileExists(ManifestPath(projectDirectory));

    public (ProjectManifest Manifest, string Path) ReadManifestOrThrow(string projectDirectory) {
        var path = ManifestPath(projectDirectory);
        if (!_fileSystem.FileExists(path)) {
            throw new WorkflowException($"No {ManifestFileName} found in {projectDirectory}. Run 'dotquay init' first.");
        }

        return (ManifestReader.ReadProject(_fileSystem.ReadAllText(path), path), path);
    }

    /// <summary>A missing lockfile is not an error for read-only and additive operations — an
    /// initialised-but-never-installed project simply has nothing locked yet.</summary>
    public (DotquayLockfile Lockfile, string Path) ReadOrCreateLockfile(string projectDirectory) {
        var path = LockfilePath(projectDirectory);
        return _fileSystem.FileExists(path)
            ? (LockfileReader.Read(_fileSystem.ReadAllText(path), path), path)
            : (new DotquayLockfile(), path);
    }

    /// <summary>For <c>install</c>, where "no lockfile" means the reproducibility contract can't be
    /// honoured at all (SPEC.md §5) rather than "nothing to do".</summary>
    public (DotquayLockfile Lockfile, string Path) ReadLockfileOrThrow(string projectDirectory) {
        var path = LockfilePath(projectDirectory);
        if (!_fileSystem.FileExists(path)) {
            throw new WorkflowException($"No {LockfileFileName} found in {projectDirectory}. Run 'dotquay add' first.");
        }

        return (LockfileReader.Read(_fileSystem.ReadAllText(path), path), path);
    }

    public void WriteManifest(string manifestPath, ProjectManifest manifest) =>
        _fileSystem.WriteAllText(manifestPath, ManifestWriter.Serialize(manifest));

    public void WriteLockfile(string lockfilePath, DotquayLockfile lockfile) =>
        _fileSystem.WriteAllText(lockfilePath, LockfileWriter.Serialize(lockfile));
}
