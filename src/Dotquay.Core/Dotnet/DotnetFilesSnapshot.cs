using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Dotnet;

/// <summary>
/// The bytes of every file a NuGet sync writes before its restore — the <c>.csproj</c> and
/// <c>NuGet.Config</c> — so a failed restore can put them back (DESIGN-v0.21 §4).
/// </summary>
/// <remarks>
/// <para>A sync writes each <c>PackageReference</c> first and restores once at the end, and the
/// lockfile is written only after the restore succeeds. Without this, a restore that NuGet refuses
/// (NU1605: a package needs a newer dependency than the one resolved) leaves a <c>.csproj</c> that
/// matches neither the manifest nor the lockfile, and the project stops building.</para>
/// <para>Bytes, not text, so encoding and line endings come back exactly. A file that did not exist
/// is deleted again.</para>
/// </remarks>
public sealed class DotnetFilesSnapshot {
    private readonly IFileSystem _fileSystem;
    private readonly IReadOnlyList<(string Path, byte[]? Contents)> _files;

    private DotnetFilesSnapshot(IFileSystem fileSystem,
        IReadOnlyList<(string Path, byte[]? Contents)> files) {
        _fileSystem = fileSystem;
        _files = files;
    }

    public static DotnetFilesSnapshot Capture(IFileSystem fileSystem, string projectDirectory) {
        var paths = new List<string>
        {
            fileSystem.CombinePath(projectDirectory, "NuGet.Config"),
            fileSystem.CombinePath(projectDirectory, "nuget.config"),
        };

        // No .csproj (or several) is the installer's error to report, with its own message; there
        // is nothing of it to snapshot here.
        try {
            paths.Add(CsprojLocator.Locate(fileSystem, projectDirectory));
        } catch (DotnetSyncException) {
        }

        var files = paths
            .Distinct(StringComparer.Ordinal)
            .Select(path =>
                (path, fileSystem.FileExists(path) ? fileSystem.ReadAllBytes(path) : null))
            .ToList();

        return new DotnetFilesSnapshot(fileSystem, files);
    }

    /// <summary>Puts every captured file back as it was.</summary>
    public void Restore() {
        foreach (var (path, contents) in _files) {
            if (contents is not null) {
                _fileSystem.WriteAllBytes(path, contents);
            } else if (_fileSystem.FileExists(path)) {
                _fileSystem.DeleteFile(path);
            }
        }
    }
}
