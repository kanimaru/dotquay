using Dependi.Core.Abstractions;

namespace Dependi.Core.IO;

/// <summary>Real, disk-backed <see cref="IFileSystem"/>. Everything else in Dependi.Core takes
/// <see cref="IFileSystem"/> as a dependency instead of calling <see cref="System.IO"/> directly,
/// so this is the only type in the codebase allowed to do so.</summary>
public sealed class SystemFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, contents);
    }

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, contents);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void MoveDirectory(string source, string destination)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        var destinationParent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destinationParent))
        {
            Directory.CreateDirectory(destinationParent);
        }

        // Directory.Move is atomic when source and destination are on the same volume, which
        // holds for the cache-to-project moves the sync engine performs (SPEC.md §9 step 5).
        Directory.Move(source, destination);
    }

    public IEnumerable<string> EnumerateFiles(string path, bool recursive = true) =>
        Directory.EnumerateFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

    public IEnumerable<string> EnumerateDirectories(string path, bool recursive = false) =>
        Directory.EnumerateDirectories(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

    public string CombinePath(params string[] segments) => Path.Combine(segments);

    public string GetFullPath(string path) => Path.GetFullPath(path);
}
