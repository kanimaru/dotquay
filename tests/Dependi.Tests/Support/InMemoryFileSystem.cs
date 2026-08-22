using Dependi.Core.Abstractions;

namespace Dependi.Tests.Support;

/// <summary>
/// In-memory <see cref="IFileSystem"/> test double. Keeps <c>Dependi.Core</c> tests fast and
/// hermetic (SPEC.md §12: "testable without network/disk") — no temp directories, no cleanup, no
/// tests that leak state into each other via a shared real filesystem.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    // Bytes are the single source of truth — ReadAllText/WriteAllText are just UTF-8 views over
    // the same store, so a file written as bytes is visible as text and vice versa, the way a
    // real filesystem behaves.
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { "" };

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public bool DirectoryExists(string path) => _directories.Contains(Normalize(path));

    public string ReadAllText(string path) => System.Text.Encoding.UTF8.GetString(ReadAllBytes(path));

    public void WriteAllText(string path, string contents) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(contents));

    public byte[] ReadAllBytes(string path)
    {
        var key = Normalize(path);
        if (!_files.TryGetValue(key, out var contents))
        {
            throw new FileNotFoundException($"No such file in the in-memory filesystem: {path}", path);
        }

        return contents;
    }

    public void WriteAllBytes(string path, byte[] contents)
    {
        var key = Normalize(path);
        CreateDirectory(ParentOf(key));
        _files[key] = contents;
    }

    public void CreateDirectory(string path)
    {
        var current = "";
        _directories.Add(current);

        foreach (var segment in Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? segment : $"{current}/{segment}";
            _directories.Add(current);
        }
    }

    public void DeleteDirectory(string path)
    {
        var key = Normalize(path);
        _directories.RemoveWhere(d => d == key || d.StartsWith(key + "/", StringComparison.Ordinal));

        foreach (var file in _files.Keys.Where(f => f == key || f.StartsWith(key + "/", StringComparison.Ordinal)).ToList())
        {
            _files.Remove(file);
        }
    }

    public void DeleteFile(string path) => _files.Remove(Normalize(path));

    public void MoveDirectory(string source, string destination)
    {
        var sourceKey = Normalize(source);
        var destinationKey = Normalize(destination);

        DeleteDirectory(destinationKey);

        foreach (var file in _files.Keys.Where(f => f == sourceKey || f.StartsWith(sourceKey + "/", StringComparison.Ordinal)).ToList())
        {
            var suffix = file[sourceKey.Length..];
            _files[destinationKey + suffix] = _files[file];
            _files.Remove(file);
        }

        foreach (var dir in _directories.Where(d => d == sourceKey || d.StartsWith(sourceKey + "/", StringComparison.Ordinal)).ToList())
        {
            var suffix = dir[sourceKey.Length..];
            _directories.Add(destinationKey + suffix);
            _directories.Remove(dir);
        }

        CreateDirectory(ParentOf(destinationKey));
    }

    public IEnumerable<string> EnumerateFiles(string path, bool recursive = true)
    {
        var key = Normalize(path);
        var prefix = key.Length == 0 ? "" : key + "/";

        foreach (var file in _files.Keys)
        {
            if (file != key && !file.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!recursive && file.Length > prefix.Length && file[prefix.Length..].Contains('/'))
            {
                continue;
            }

            yield return file;
        }
    }

    public IEnumerable<string> EnumerateDirectories(string path, bool recursive = false)
    {
        var key = Normalize(path);
        var prefix = key.Length == 0 ? "" : key + "/";

        foreach (var dir in _directories)
        {
            if (dir.Length == 0 || dir == key || !dir.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = dir[prefix.Length..];
            if (!recursive && remainder.Contains('/'))
            {
                continue;
            }

            yield return dir;
        }
    }

    public string CombinePath(params string[] segments) =>
        string.Join('/', segments.Select(Normalize).Where(s => s.Length > 0));

    public string GetFullPath(string path) => "/" + Normalize(path);

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static string ParentOf(string key)
    {
        var index = key.LastIndexOf('/');
        return index < 0 ? "" : key[..index];
    }
}
