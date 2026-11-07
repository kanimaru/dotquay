using System.Text.RegularExpressions;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Sync;

namespace Dotquay.Core.Doctor;

/// <summary>
/// Finds project code that loads an addon's resource by a bare <c>uid://</c>.
/// </summary>
/// <remarks>
/// The one reference shape that cannot survive an addon's UIDs changing. Godot's scene and resource
/// files record a <c>uid</c> next to a <c>path</c>, so a stale uid there falls back to the path and
/// is repaired on the next save; a <c>uid://</c> string literal in a script has nothing to fall
/// back to.
/// <para>
/// Read-only by design. dotquay reports these and stops: fixing them means rewriting the project's
/// own scripts, and the CLI's contract is that it writes <c>dotquay.toml</c>, <c>dotquay.lock</c>,
/// and <c>addons/</c> — nothing else (SPEC.md §18).
/// </para>
/// </remarks>
public static partial class UidReferenceScanner {
    /// <param name="File">Project-relative path of the script holding the reference.</param>
    /// <param name="Uid">The <c>uid://…</c> value, as written.</param>
    /// <param name="Target">The addon-relative file it resolves to.</param>
    public sealed record Finding(string File, string Uid, string Target);

    /// <summary>Where the scan looks for references. Scene and resource files are deliberately
    /// absent: they carry a path alongside the uid, so they self-heal.</summary>
    private static readonly string[] CodeExtensions = [".gd", ".cs"];

    /// <summary>
    /// Returns every bare <c>uid://</c> in project code that resolves to a file inside
    /// <paramref name="addonsDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Only references that actually land in an addon are reported. A project's uid reference to
    /// its own resource is nobody's problem — dotquay never touches those files — and reporting
    /// them would bury the handful that matter.
    /// </remarks>
    public static IReadOnlyList<Finding> Scan(IFileSystem fileSystem, string projectDirectory,
        string addonsDirectory) {
        if (!fileSystem.DirectoryExists(addonsDirectory)) {
            return [];
        }

        var owners = MapUidsToAddonFiles(fileSystem, addonsDirectory);
        if (owners.Count == 0) {
            return [];
        }

        var findings = new List<Finding>();
        var visibility = new GodotVisibility(fileSystem, projectDirectory);

        foreach (var file in fileSystem.EnumerateFiles(projectDirectory, recursive: true)) {
            var relative = Relative(file, projectDirectory);

            // Code *inside* an addon referencing that addon's own resources is the addon author's
            // business, and it moves in lockstep with the uids it names. Code Godot never scans
            // (.godot/, .dotquay/staging/, a .gdignore'd folder) is never loaded, so it cannot
            // break.
            if (!IsCode(relative)
                || relative.StartsWith("addons/", StringComparison.OrdinalIgnoreCase)
                || !visibility.IsVisible(relative)) {
                continue;
            }

            foreach (Match match in UidPattern().Matches(fileSystem.ReadAllText(file))) {
                var uid = match.Value;
                if (owners.TryGetValue(uid, out var target)) {
                    findings.Add(new Finding(relative, uid, target));
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Builds uid → addon file from the two places an addon's uids are recorded: the <c>.uid</c>
    /// sidecar beside a script, and the <c>uid=</c> line in an asset's <c>.import</c>.
    /// </summary>
    private static Dictionary<string, string> MapUidsToAddonFiles(IFileSystem fileSystem,
        string addonsDirectory) {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var visibility = new GodotVisibility(fileSystem, addonsDirectory);

        foreach (var file in fileSystem.EnumerateFiles(addonsDirectory, recursive: true)) {
            var relative = Relative(file, addonsDirectory);

            // Godot never registers a uid it never scanned, so nothing can load one by it.
            if (!visibility.IsVisible(relative)) {
                continue;
            }

            // A .uid file is exactly one line: the uid of the script it sits beside.
            if (relative.EndsWith(".uid", StringComparison.OrdinalIgnoreCase)) {
                var uid = fileSystem.ReadAllText(file).Trim();
                if (uid.StartsWith("uid://", StringComparison.Ordinal)) {
                    owners[uid] = relative[..^".uid".Length];
                }

                continue;
            }

            if (!relative.EndsWith(".import", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var declared = UidPattern().Match(fileSystem.ReadAllText(file));
            if (declared.Success) {
                owners[declared.Value] = relative[..^".import".Length];
            }
        }

        return owners;
    }

    /// <summary>
    /// Matches a whole <c>uid://</c> token.
    /// </summary>
    /// <remarks>
    /// Anchored on the full run of identifier characters rather than matched as a substring,
    /// because <c>uid://abc</c> is a prefix of <c>uid://abc1</c> — treating one as the other is how
    /// a scan reports a reference to a resource nobody mentioned.
    /// </remarks>
    [GeneratedRegex(@"uid://[a-zA-Z0-9]+", RegexOptions.Compiled)]
    private static partial Regex UidPattern();

    private static bool IsCode(string relativePath) =>
        CodeExtensions.Any(extension =>
            relativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static string Relative(string filePath, string root) =>
        filePath[root.Length..].TrimStart('/', '\\').Replace('\\', '/');
}
