using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Doctor;

/// <summary>
/// Detects an addon subtree that is a Godot <em>project</em> root rather than an addon.
/// </summary>
/// <remarks>
/// <para>
/// A manifest's <c>path</c> names the subtree of the addon's repository that gets copied into
/// <c>addons/&lt;folder&gt;</c>. When that repository is restructured — the addon moved from the
/// root into <c>addons/&lt;name&gt;/</c> so the root can become a playable Godot project — every
/// manifest that still says <c>path = "."</c> keeps resolving and installing without a single
/// error. The subtree exists; it just now holds <c>project.godot</c>, a nested <c>addons/</c>, and
/// no <c>plugin.cfg</c> where Godot expects one. The only symptom is the editor reporting the
/// plugin as missing, and <c>install</c>, which replays the lockfile, reproduces it faithfully.
/// </para>
/// <para>
/// Real incident (2026-09-18): vrm-addon moved its plugin to <c>addons/vrm</c>; vbeami's package
/// manifest still declared it with <c>path = "."</c>, so every consumer of vbeami received the
/// whole vrm-addon repository under <c>addons/vrm</c>. Nothing was invalid, so nothing reported it.
/// </para>
/// <para>
/// The test is deliberately structural — <c>project.godot</c> at the subtree root and no
/// <c>plugin.cfg</c> beside it — because those two files are exactly what Godot itself uses to tell
/// a project from an addon. An addon that ships a <c>project.godot</c> next to its own
/// <c>plugin.cfg</c> is still an addon and is left alone; an addon that ships neither is a plain
/// content folder (assets, shaders) and is also left alone, since nothing here says it is a
/// project.
/// </para>
/// </remarks>
public static class GodotProjectRootChecker {
    /// <summary>The file Godot writes at, and only at, a project's root.</summary>
    public const string ProjectMarker = "project.godot";

    /// <summary>The file Godot requires at an editor plugin's root.</summary>
    public const string PluginMarker = "plugin.cfg";

    /// <summary>Where a Godot project keeps its plugins — and so where the real addon is most
    /// likely hiding when a subtree turns out to be a project.</summary>
    public const string AddonsFolder = "addons";

    /// <param name="Candidates">Every <c>addons/&lt;folder&gt;</c> inside the checked tree that
    /// holds a <c>plugin.cfg</c> — the places the real addon could be — as repository-relative
    /// subtree paths (so already prefixed with the configured <c>path</c> when that isn't the
    /// root), sorted ordinally so a message lists them stably.</param>
    /// <param name="SuggestedPath">The candidate whose folder matches the addon's name (ignoring
    /// case, as the folder-case correction does), or the only candidate when there is exactly one;
    /// null when the tree gives no clear answer and the message has to fall back to
    /// listing.</param>
    public sealed record Finding(IReadOnlyList<string> Candidates, string? SuggestedPath) {
        /// <summary>Whether the tree offered any plugin at all to point at.</summary>
        public bool HasCandidates => Candidates.Count > 0;
    }

    /// <summary>
    /// Returns a finding when <paramref name="treePath"/> is a Godot project root instead of an
    /// addon, or null when it is an addon, a plain content folder, or not there at all.
    /// </summary>
    /// <param name="fileSystem">The filesystem the tree lives on.</param>
    /// <param name="treePath">The extracted or installed subtree — the discovery staging folder
    /// during resolution, <c>addons/&lt;folder&gt;</c> for <c>doctor</c>.</param>
    /// <param name="addonName">The addon's manifest name, used to pick the candidate that is most
    /// likely the addon itself.</param>
    /// <param name="configuredPath">The manifest's <c>path</c> for this addon, so that candidates
    /// are expressed as the value <c>path</c> should be set to rather than relative to a subtree
    /// the reader would then have to prepend by hand. <c>"."</c>, empty, or null all mean the
    /// repository root.</param>
    public static Finding? Check(IFileSystem fileSystem, string treePath, string addonName,
        string? configuredPath = null) {
        if (!fileSystem.DirectoryExists(treePath)
            || !fileSystem.FileExists(fileSystem.CombinePath(treePath, ProjectMarker))
            || fileSystem.FileExists(fileSystem.CombinePath(treePath, PluginMarker))) {
            return null;
        }

        var prefix = NormalizeRelative(configuredPath);
        var candidates = new List<string>();

        var addonsPath = fileSystem.CombinePath(treePath, AddonsFolder);
        if (fileSystem.DirectoryExists(addonsPath)) {
            foreach (var directory in fileSystem.EnumerateDirectories(addonsPath)) {
                if (fileSystem.FileExists(fileSystem.CombinePath(directory, PluginMarker))) {
                    var relative = AddonsFolder + "/" + LastSegment(directory);
                    candidates.Add(prefix.Length == 0 ? relative : prefix + "/" + relative);
                }
            }
        }

        candidates.Sort(StringComparer.Ordinal);

        var suggested = candidates.FirstOrDefault(
                candidate =>
                    string.Equals(LastSegment(candidate), addonName,
                        StringComparison.OrdinalIgnoreCase))
            ?? (candidates.Count == 1 ? candidates[0] : null);

        return new Finding(candidates, suggested);
    }

    /// <summary>
    /// The <c>dotquay override</c> invocation that pins <paramref name="addonName"/> to
    /// <paramref name="path"/> from the consuming project, without waiting for whoever declares the
    /// addon wrongly to fix and re-release their manifest.
    /// </summary>
    /// <remarks>
    /// <c>override</c> insists on exactly one source flag, so the caller supplies the one it knows
    /// (<c>--version</c>/<c>--branch</c> from the declaring spec during resolution; <c>--rev</c>
    /// with the locked commit from <c>doctor</c>, which has nothing else to go on). Spelled out
    /// here rather than at each call site so the two messages can never drift into quoting
    /// different commands.
    /// </remarks>
    public static string OverrideCommand(string addonName, string sourceFlag, string sourceValue,
        string path) =>
        $"dotquay override {addonName} {sourceFlag} {Quote(sourceValue)} --path {Quote(path)}";

    /// <summary>The clause that tells the reader where the real addon is, or that there is no
    /// obvious place — shared by the resolver's warning and <c>doctor</c>'s issue so both describe
    /// the same tree the same way.</summary>
    public static string DescribeWhereTheAddonIs(Finding finding) {
        if (finding.SuggestedPath is not null) {
            return $"the addon itself sits at '{finding.SuggestedPath}' inside its repository";
        }

        return finding.HasCandidates
            ? $"its repository holds several plugins "
            + $"({string.Join(", ", finding.Candidates.Select(c => $"'{c}'"))}) and none is "
            + $"obviously this one"
            : "its repository holds no addons/*/plugin.cfg at all";
    }

    /// <summary>A shell-safe rendering: bare when the value is plain, quoted when it carries
    /// anything a shell or a version range would otherwise chew on (<c>~1.4</c>, spaces,
    /// <c>&gt;=</c>).</summary>
    private static string Quote(string value) =>
        value.All(c => char.IsLetterOrDigit(c) || c is '.' or '/' or '-' or '_') ? value
        : $"\"{value}\"";

    private static string NormalizeRelative(string? path) {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }

    private static string LastSegment(string path) {
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }
}
