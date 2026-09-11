using Dotquay.Core.Abstractions;

namespace Dotquay.Tests.Support;

/// <summary>
/// In-memory <see cref="IFileSystem"/> test double. Keeps <c>Dotquay.Core</c> tests fast and
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

    // Directory links, keyed by the link's own path. Modelled rather than stubbed out because the
    // behaviour that actually matters about links is what happens when something *deletes* one
    // (SPEC.md §7.4) — a fake that pretended links were ordinary directories would make the one
    // dangerous case untestable.
    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);

    public bool FileExists(string path) => _files.ContainsKey(Resolve(path));

    public bool DirectoryExists(string path) => _directories.Contains(Resolve(path));

    public string ReadAllText(string path) => System.Text.Encoding.UTF8.GetString(ReadAllBytes(path));

    public void WriteAllText(string path, string contents) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(contents));

    public byte[] ReadAllBytes(string path)
    {
        var key = Resolve(path);
        if (!_files.TryGetValue(key, out var contents))
        {
            throw new FileNotFoundException($"No such file in the in-memory filesystem: {path}", path);
        }

        return contents;
    }

    public void WriteAllBytes(string path, byte[] contents)
    {
        var key = Resolve(path);
        GuardWritable(key);
        CreateDirectory(ParentOf(key));
        _files[key] = contents;
    }

    /// <summary>Directories this fake refuses to be written into, as a real one refuses a directory
    /// the process does not own. Modelled rather than stubbed because the self-update writability
    /// guard (docs/DESIGN-v0.17-SELF-UPDATE.md) exists precisely to turn that refusal into an
    /// explanation, and a fake that always succeeds cannot test an explanation.</summary>
    public HashSet<string> ReadOnlyDirectories { get; } = new(StringComparer.Ordinal);

    /// <summary>Files that had <see cref="SetExecutablePermissions"/> called on them while they
    /// existed, and that kept it across a move - which is the behaviour a downloaded binary depends
    /// on and the only reason the bit is modelled at all.</summary>
    public HashSet<string> ExecutableFiles { get; } = new(StringComparer.Ordinal);

    private void GuardWritable(string key)
    {
        var parent = ParentOf(key);

        foreach (var directory in ReadOnlyDirectories)
        {
            var normalized = Normalize(directory);
            if (parent == normalized || parent.StartsWith(normalized + "/", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException($"Access to the path '{key}' is denied.");
            }
        }
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

        // Deleting a link removes the link and nothing else. The target is somebody's own checkout of
        // the addon; recursing into it would delete their working copy, uncommitted work included.
        if (_links.Remove(key))
        {
            _directories.Remove(key);
            return;
        }

        _directories.RemoveWhere(d => d == key || d.StartsWith(key + "/", StringComparison.Ordinal));

        foreach (var file in _files.Keys.Where(f => f == key || f.StartsWith(key + "/", StringComparison.Ordinal)).ToList())
        {
            _files.Remove(file);
        }
    }

    public bool IsDirectoryLink(string path) => _links.ContainsKey(Normalize(path));

    public string? ResolveDirectoryLinkTarget(string path) =>
        _links.TryGetValue(Normalize(path), out var target) ? GetFullPath(target) : null;

    public void CreateDirectoryLink(string linkPath, string targetPath)
    {
        var key = Normalize(linkPath);

        DeleteDirectory(key);
        CreateDirectory(ParentOf(key));

        _links[key] = Normalize(targetPath);
        _directories.Add(key);
    }

    public void DeleteFile(string path) => _files.Remove(Normalize(path));

    public void MoveFile(string source, string destination, bool overwrite = false)
    {
        var from = Resolve(source);
        var to = Resolve(destination);

        if (!_files.TryGetValue(from, out var contents))
        {
            throw new FileNotFoundException($"No such file in the in-memory filesystem: {source}", source);
        }

        if (!overwrite && _files.ContainsKey(to))
        {
            throw new IOException($"The file '{destination}' already exists.");
        }

        GuardWritable(to);
        CreateDirectory(ParentOf(to));

        _files[to] = contents;
        _files.Remove(from);

        if (ExecutableFiles.Remove(from))
        {
            ExecutableFiles.Add(to);
        }
    }

    public void SetExecutablePermissions(string path)
    {
        var key = Resolve(path);

        // Matches the real implementation, which returns early for a missing file.
        if (_files.ContainsKey(key))
        {
            ExecutableFiles.Add(key);
        }
    }

    /// <summary>Paths that had <see cref="SetOwnerOnlyPermissions"/> called on them while they
    /// existed. Modelled rather than stubbed, so "the credential file is written owner-only" is a
    /// real assertion instead of a code review.</summary>
    public HashSet<string> OwnerOnlyPaths { get; } = new(StringComparer.Ordinal);

    public void SetOwnerOnlyPermissions(string path)
    {
        var key = Normalize(path);

        // Matches the real implementation, which returns early for a missing file — so a store that
        // set permissions before writing would fail this fake exactly as it fails on disk.
        if (_files.ContainsKey(key))
        {
            OwnerOnlyPaths.Add(key);
        }
    }

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

    /// <summary>Every file that exists, anywhere. Not part of <see cref="IFileSystem"/> — it backs
    /// whole-filesystem assertions like <c>SecretLeakTests</c>'s "the token is in no file dotquay
    /// wrote", which has to check files it doesn't know the names of.</summary>
    public IEnumerable<string> AllFilePaths() => _files.Keys.ToList();

    public IEnumerable<string> EnumerateFiles(string path, bool recursive = true)
    {
        var key = Resolve(path);
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
        var key = Resolve(path);
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

    /// <summary>
    /// Normalises, then redirects through any directory link on the path — so reading
    /// <c>addons/foo/thing.gd</c> when <c>addons/foo</c> is linked returns the file from the link's
    /// target, exactly as a real filesystem would.
    /// </summary>
    /// <remarks>
    /// This is what makes "a linked addon shows the developer's live edits" a real assertion rather
    /// than a mocked one. Deliberately not applied to the link's own bookkeeping (create, delete,
    /// and the link test itself), which must act on the literal path.
    /// </remarks>
    private string Resolve(string path)
    {
        var key = Normalize(path);

        if (_links.Count == 0)
        {
            return key;
        }

        foreach (var (link, target) in _links)
        {
            if (key == link)
            {
                return target;
            }

            if (key.StartsWith(link + "/", StringComparison.Ordinal))
            {
                return target + key[link.Length..];
            }
        }

        return key;
    }

    private static string ParentOf(string key)
    {
        var index = key.LastIndexOf('/');
        return index < 0 ? "" : key[..index];
    }
}
