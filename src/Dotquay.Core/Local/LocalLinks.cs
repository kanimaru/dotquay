using Dotquay.Core.Abstractions;
using Tomlyn;
using Tomlyn.Model;

namespace Dotquay.Core.Local;

/// <summary>One addon redirected to a folder on this machine.</summary>
public sealed record LocalLink(string Name, string Path);

/// <summary>
/// Reads and writes <c>dotquay.local.toml</c> — the per-machine file that points an addon at a local
/// folder instead of its upstream repository (SPEC.md §7.4).
/// </summary>
/// <remarks>
/// <para>
/// Solves the addon-author's loop. Developing an addon while a project consumes it otherwise means
/// commit → push → <c>dotquay update</c> for every change, because resolution is commit-based by
/// design (SPEC.md §5). A link installs <c>addons/&lt;folder&gt;</c> as a directory link into the
/// working checkout, so an edit is visible to Godot on the next reload with no dotquay command at all.
/// </para>
/// <para>
/// Deliberately a <em>separate, gitignored</em> file rather than a key in <c>dotquay.toml</c>. The
/// value is an absolute path on one person's disk: committing it would break every teammate and every
/// CI run, and it would make the lockfile's reproducibility claim (goal #1) a lie. Same reasoning as
/// the editor plugin's CLI path being an editor setting rather than a project setting.
/// </para>
/// </remarks>
public sealed class LocalLinks
{
    public const string FileName = "dotquay.local.toml";

    private const string SectionKey = "link";

    private readonly IFileSystem _fileSystem;

    public LocalLinks(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public string PathFor(string projectDirectory) => _fileSystem.CombinePath(projectDirectory, FileName);

    /// <summary>
    /// Every link declared for this project, keyed by addon name. Empty when the file is absent — the
    /// overwhelmingly common case, and not something to report.
    /// </summary>
    /// <remarks>
    /// A malformed file is a hard error rather than a shrug: it exists only because someone
    /// deliberately created it, so silently ignoring it would leave them staring at an addon that
    /// refuses to link with nothing said about why.
    /// </remarks>
    public IReadOnlyDictionary<string, LocalLink> Read(string projectDirectory)
    {
        var path = PathFor(projectDirectory);
        if (!_fileSystem.FileExists(path))
        {
            return new Dictionary<string, LocalLink>(StringComparer.Ordinal);
        }

        TomlTable table;
        try
        {
            table = TomlSerializer.Deserialize<TomlTable>(_fileSystem.ReadAllText(path), new TomlSerializerOptions { SourceName = path })
                ?? new TomlTable();
        }
        catch (TomlException ex)
        {
            throw new LocalLinkException($"{path}: invalid TOML.{Environment.NewLine}{ex.Message}", ex);
        }

        var result = new Dictionary<string, LocalLink>(StringComparer.Ordinal);

        if (!table.TryGetValue(SectionKey, out var raw) || raw is not TomlTable links)
        {
            return result;
        }

        foreach (var name in links.Keys)
        {
            if (links[name] is not TomlTable entry)
            {
                throw new LocalLinkException($"{path}: [{SectionKey}.{name}] must be a table with a 'path' key.");
            }

            if (!entry.TryGetValue("path", out var pathValue) || pathValue is not string localPath || localPath.Length == 0)
            {
                throw new LocalLinkException($"{path}: [{SectionKey}.{name}] is missing required key 'path'.");
            }

            result[name] = new LocalLink(name, localPath);
        }

        return result;
    }

    /// <summary>Adds or replaces one link and writes the file back.</summary>
    public void Set(string projectDirectory, string name, string localPath)
    {
        var resolved = _fileSystem.GetFullPath(localPath);

        if (!_fileSystem.DirectoryExists(resolved))
        {
            throw new LocalLinkException(
                $"'{resolved}' is not a directory. A link points at the addon folder itself — the one whose "
                + "contents should appear under addons/ — not at the repository root unless they are the same.");
        }

        var links = new Dictionary<string, LocalLink>(Read(projectDirectory), StringComparer.Ordinal)
        {
            [name] = new LocalLink(name, resolved),
        };

        Write(projectDirectory, links);
    }

    /// <summary>Removes one link. Returns false when there was nothing to remove, so a caller can say
    /// so rather than reporting a change that didn't happen.</summary>
    public bool Remove(string projectDirectory, string name)
    {
        var links = new Dictionary<string, LocalLink>(Read(projectDirectory), StringComparer.Ordinal);

        if (!links.Remove(name))
        {
            return false;
        }

        Write(projectDirectory, links);
        return true;
    }

    private void Write(string projectDirectory, IReadOnlyDictionary<string, LocalLink> links)
    {
        var path = PathFor(projectDirectory);

        if (links.Count == 0)
        {
            // An empty file is indistinguishable from no links, and leaving one behind means the next
            // person to open the project finds a mysterious empty file suggesting something is
            // configured when nothing is.
            _fileSystem.DeleteFile(path);
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Dotquay local links — per-machine, NOT for committing (SPEC.md §7.4).");
        sb.AppendLine("# Each entry replaces an addon's normal install with a directory link into a folder on");
        sb.AppendLine("# this machine, so edits there are live in this project with no commit and no update.");
        sb.AppendLine("# Add dotquay.local.toml to .gitignore.");

        foreach (var (name, link) in links.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.AppendLine();
            sb.AppendLine($"[{SectionKey}.{FormatKey(name)}]");
            sb.AppendLine($"path = {Quote(link.Path)}");
        }

        _fileSystem.WriteAllText(path, sb.ToString());
    }

    private static string FormatKey(string key) =>
        key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? key : Quote(key);

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <summary>Thrown for a malformed or unusable <c>dotquay.local.toml</c>.</summary>
public sealed class LocalLinkException : Exception
{
    public LocalLinkException(string message)
        : base(message)
    {
    }

    public LocalLinkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
