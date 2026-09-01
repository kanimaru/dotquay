using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Doctor;

/// <summary>
/// Detects an addon installed under a folder whose casing its own files disagree with.
/// </summary>
/// <remarks>
/// A Godot addon refers to itself by absolute <c>res://addons/&lt;folder&gt;/</c> paths. Those are
/// case-sensitive strings, even on a filesystem that is not — so an addon installed to
/// <c>addons/Gut</c> whose files all say <c>res://addons/gut/</c> is broken in a way that looks like
/// it works: Windows finds the files, Godot then "repairs" every reference to the real casing and
/// re-saves the scenes and resources it touched. The result is an addon that reports as
/// hand-modified forever and floods the editor with errors on every open.
/// <para>
/// Worth a check rather than a comment because the symptom points nowhere near the cause. Finding
/// this by hand meant diffing an installed addon against a fresh clone of its own locked commit; the
/// evidence is sitting in the addon's own files the whole time.
/// </para>
/// </remarks>
public static class AddonFolderCaseChecker
{
    /// <param name="Folder">The folder the addon is installed in.</param>
    /// <param name="Expected">The casing its own files refer to.</param>
    /// <param name="ReferenceCount">How many of its files disagree — the scale of the repair Godot
    /// performs on every open.</param>
    public sealed record Mismatch(string Folder, string Expected, int ReferenceCount);

    /// <summary>File types that carry <c>res://</c> references. Binary assets never do.</summary>
    private static readonly string[] TextExtensions = [".gd", ".cs", ".tscn", ".tres", ".import", ".cfg", ".godot"];

    private const string Marker = "res://addons/";

    /// <summary>
    /// Returns a mismatch when the addon's own files reference a folder that differs from
    /// <paramref name="installedFolder"/> only by case.
    /// </summary>
    /// <remarks>
    /// Case-only, deliberately. An addon referencing <c>res://addons/something_else/</c> is usually
    /// referring to a genuinely different addon it depends on, which is not a fault — reporting those
    /// would bury the one finding that is.
    /// </remarks>
    public static Mismatch? Check(IFileSystem fileSystem, string addonPath, string installedFolder)
    {
        if (!fileSystem.DirectoryExists(addonPath))
        {
            return null;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in fileSystem.EnumerateFiles(addonPath, recursive: true))
        {
            if (!IsText(file))
            {
                continue;
            }

            foreach (var referenced in ReferencedFolders(fileSystem.ReadAllText(file)))
            {
                if (string.Equals(referenced, installedFolder, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(referenced, installedFolder, StringComparison.Ordinal))
                {
                    counts[referenced] = counts.GetValueOrDefault(referenced) + 1;
                }
            }
        }

        if (counts.Count == 0)
        {
            return null;
        }

        // The most-referenced spelling wins, so one stray typo upstream can't outvote the real name.
        var winner = counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First();

        return new Mismatch(installedFolder, winner.Key, winner.Value);
    }

    /// <summary>Pulls the folder segment out of every <c>res://addons/&lt;folder&gt;/</c> in the
    /// text.</summary>
    private static IEnumerable<string> ReferencedFolders(string content)
    {
        var index = content.IndexOf(Marker, StringComparison.Ordinal);

        while (index >= 0)
        {
            var start = index + Marker.Length;
            var end = content.IndexOf('/', start);

            if (end > start)
            {
                yield return content[start..end];
            }

            index = content.IndexOf(Marker, start, StringComparison.Ordinal);
        }
    }

    private static bool IsText(string path) =>
        TextExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
