using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Sync;

/// <summary>
/// Decides whether Godot's filesystem scan would see a path at all.
/// </summary>
/// <remarks>
/// Godot skips every file and folder whose name starts with a dot, and every folder holding a
/// <c>.gdignore</c> together with everything below it. Nothing there is imported, exported, loaded or
/// offered to autocomplete, so a check that reports on it reports on something that cannot matter.
/// <c>.dotquay/staging/</c> is the case that made this a rule: an extracted copy of an addon, whose
/// own uid references <c>doctor</c> reported as if the project had written them.
/// <para>
/// Every scan that walks a project on the user's behalf asks this, rather than listing the folders it
/// happens to know about — a list is how <c>.dotquay/</c> got missed when <c>.godot/</c> was not.
/// </para>
/// </remarks>
public sealed class GodotVisibility
{
    private readonly IFileSystem _fileSystem;
    private readonly string _root;

    /// <summary>Folder (relative, forward-slashed) → whether it holds a <c>.gdignore</c>. Every file
    /// below a folder asks about the same ancestors, so each is looked up once.</summary>
    private readonly Dictionary<string, bool> _gdignored = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="root">The folder <c>relativePath</c> arguments are relative to; usually the
    /// project directory.</param>
    public GodotVisibility(IFileSystem fileSystem, string root)
    {
        _fileSystem = fileSystem;
        _root = root;
    }

    /// <summary>True when any segment of <paramref name="relativePath"/> starts with a dot.</summary>
    public static bool IsHidden(string relativePath) =>
        relativePath.Replace('\\', '/').Split('/').Any(segment => segment.StartsWith('.'));

    /// <summary>
    /// True when Godot would scan <paramref name="relativePath"/>.
    /// </summary>
    /// <param name="isDirectory">True when the path names a folder, which is then itself checked for a
    /// <c>.gdignore</c>; a file is only checked against the folders above it.</param>
    public bool IsVisible(string relativePath, bool isDirectory = false)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (IsHidden(normalized))
        {
            return false;
        }

        var segments = normalized.Split('/');
        var folders = isDirectory ? segments.Length : segments.Length - 1;
        var folder = "";

        for (var i = 0; i < folders; i++)
        {
            folder = folder.Length == 0 ? segments[i] : $"{folder}/{segments[i]}";

            if (!_gdignored.TryGetValue(folder, out var ignored))
            {
                ignored = _fileSystem.FileExists(_fileSystem.CombinePath([_root, .. folder.Split('/'), ".gdignore"]));
                _gdignored[folder] = ignored;
            }

            if (ignored)
            {
                return false;
            }
        }

        return true;
    }
}
