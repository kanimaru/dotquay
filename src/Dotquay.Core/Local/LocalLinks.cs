using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Dotnet.SourceBuild;
using Dotquay.Core.Toml;
using Tomlyn;
using Tomlyn.Model;

namespace Dotquay.Core.Local;

/// <summary>One addon redirected to a folder on this machine.</summary>
public sealed record LocalLink(string Name, string Path);

/// <summary>Everything <c>dotquay.local.toml</c> declares, split by what it redirects.</summary>
public sealed record LocalLinkSet(
    IReadOnlyDictionary<string, LocalLink> Addons,
    IReadOnlyDictionary<string, DotnetLink> Dotnet) {
    public static readonly LocalLinkSet Empty = new(
        new Dictionary<string, LocalLink>(StringComparer.Ordinal),
        new Dictionary<string, DotnetLink>(StringComparer.Ordinal));

    public bool IsEmpty => Addons.Count == 0 && Dotnet.Count == 0;
}

/// <summary>
/// Reads and writes <c>dotquay.local.toml</c> — the per-machine file that points an addon at a
/// local folder instead of its upstream repository (SPEC.md §7.4), and a <c>[dotnet.*]</c> package
/// at a local <c>.csproj</c> instead of a published package (v0.11).
/// </summary>
/// <remarks>
/// <para>
/// Solves the author's loop. Developing an addon or a shared library while a project consumes it
/// otherwise means commit → push → <c>dotquay update</c> for every change, because resolution is
/// commit-based by design (SPEC.md §5). An addon link installs <c>addons/&lt;folder&gt;</c> as a
/// directory link into the working checkout; a dotnet link generates an MSBuild overlay that swaps
/// the <c>PackageReference</c> for a <c>ProjectReference</c>. Either way an edit is live with no
/// dotquay command at all.
/// </para>
/// <para>
/// Deliberately a <em>separate, gitignored</em> file rather than a key in <c>dotquay.toml</c>. The
/// value is an absolute path on one person's disk: committing it would break every teammate and
/// every CI run, and it would make the lockfile's reproducibility claim (goal #1) a lie. Same
/// reasoning as the editor plugin's CLI path being an editor setting rather than a project setting
/// — and the same reasoning is why a dotnet link never touches the committed <c>.csproj</c>.
/// </para>
/// <para>
/// The v0.6 file had one flat <c>[link.&lt;name&gt;]</c> section. v0.11 namespaces it into
/// <c>[link.addon.*]</c> and <c>[link.dotnet.*]</c>, mirroring <c>dotquay.toml</c>'s own
/// <c>[addons.*]</c>/<c>[dotnet.*]</c> split, because an addon and a package could otherwise
/// collide on a name. The old shape is still read as an addon link — the file never leaves one
/// machine, so there is no migration to coordinate, but silently forgetting someone's existing link
/// would leave them staring at an addon that stopped being linked with nothing said about why.
/// </para>
/// </remarks>
public sealed class LocalLinks {
    public const string FileName = "dotquay.local.toml";

    private const string SectionKey = "link";
    private const string AddonSection = "addon";
    private const string DotnetSection = "dotnet";

    private readonly IFileSystem _fileSystem;

    public LocalLinks(IFileSystem fileSystem) {
        _fileSystem = fileSystem;
    }

    public string PathFor(string projectDirectory) =>
        _fileSystem.CombinePath(projectDirectory, FileName);

    /// <summary>
    /// Every addon link declared for this project, keyed by addon name. Empty when the file is
    /// absent — the overwhelmingly common case, and not something to report.
    /// </summary>
    public IReadOnlyDictionary<string, LocalLink> Read(string projectDirectory) =>
        ReadAll(projectDirectory).Addons;

    /// <summary>Every dotnet link declared for this project, keyed by package ID.</summary>
    public IReadOnlyDictionary<string, DotnetLink> ReadDotnet(string projectDirectory) =>
        ReadAll(projectDirectory).Dotnet;

    /// <summary>
    /// Both halves of the file at once.
    /// </summary>
    /// <remarks>
    /// A malformed file is a hard error rather than a shrug: it exists only because someone
    /// deliberately created it, so silently ignoring it would leave them staring at a link that
    /// refuses to apply with nothing said about why.
    /// </remarks>
    public LocalLinkSet ReadAll(string projectDirectory) {
        var path = PathFor(projectDirectory);
        if (!_fileSystem.FileExists(path)) {
            return LocalLinkSet.Empty;
        }

        TomlTable table;
        try {
            table = DotquayToml.Parse(_fileSystem.ReadAllText(path), path);
        } catch (TomlException ex) {
            throw new LocalLinksParseException($"{path}: invalid "
                + $"TOML.{Environment.NewLine}{ex.Message}", ex);
        }

        var addons = new Dictionary<string, LocalLink>(StringComparer.Ordinal);
        var dotnet = new Dictionary<string, DotnetLink>(StringComparer.Ordinal);

        if (!table.TryGetValue(SectionKey, out var raw) || raw is not TomlTable links) {
            return LocalLinkSet.Empty;
        }

        foreach (var key in links.Keys) {
            if (links[key] is not TomlTable entry) {
                throw new LocalLinksParseException($"{path}: [{SectionKey}.{key}] must be a table "
                    + $"with a 'path' key.");
            }

            switch (key) {
                case AddonSection:
                    foreach (var (name, addonEntry) in Leaves(entry, path,
                        $"{SectionKey}.{AddonSection}")) {
                        addons[name] = new LocalLink(name,
                            RequirePath(addonEntry, path, $"{SectionKey}.{AddonSection}.{name}"));
                    }

                    break;

                case DotnetSection:
                    foreach (var (id, dotnetEntry) in Leaves(entry, path,
                        $"{SectionKey}.{DotnetSection}")) {
                        dotnet[id] = new DotnetLink(
                            id,
                            RequirePath(dotnetEntry, path, $"{SectionKey}.{DotnetSection}.{id}"),
                            dotnetEntry.TryGetValue("analyzer", out var analyzer)
                            && analyzer is true);
                    }

                    break;

                default:
                    // The v0.6 flat shape. An addon genuinely called "addon" or "dotnet" is
                    // unreachable here, which is the price of reading both layouts and is why the
                    // writer only ever emits the namespaced one.
                    addons[key] = new LocalLink(key,
                        RequirePath(entry, path, $"{SectionKey}.{key}"));
                    break;
            }
        }

        return new LocalLinkSet(addons, dotnet);
    }

    /// <summary>
    /// Walks nested tables and yields every table carrying a <c>path</c> key, reassembling the
    /// dotted name from the route taken to reach it.
    /// </summary>
    /// <remarks>
    /// The same problem <c>ManifestReader</c> solves for <c>[dotnet.&lt;PackageId&gt;]</c>: a
    /// package like <c>Kani.Core</c> written unquoted parses as nested tables rather than one key.
    /// Written separately rather than shared with that walk because the two differ in what makes a
    /// leaf and in what an unexpected key means — a helper taking both as parameters would be
    /// longer than either. dotquay's own writer always quotes the key, so this only ever matters
    /// for a hand-edit.
    /// </remarks>
    private static IEnumerable<(string Name, TomlTable Entry)> Leaves(TomlTable table,
        string sourcePath, string section) {
        foreach (var key in table.Keys) {
            if (table[key] is not TomlTable child) {
                throw new LocalLinksParseException($"{sourcePath}: [{section}.{key}] must be a "
                    + $"table with a 'path' key.");
            }

            if (child.ContainsKey("path")) {
                yield return (key, child);
                continue;
            }

            foreach (var (nested, entry) in Leaves(child, sourcePath, $"{section}.{key}")) {
                yield return ($"{key}.{nested}", entry);
            }
        }
    }

    private static string RequirePath(TomlTable entry, string sourcePath, string section) =>
        entry.TryGetValue("path", out var value) && value is string path && path.Length > 0
            ? path
            : throw new LocalLinksParseException($"{sourcePath}: [{section}] is missing required "
            + $"key 'path'.");

    /// <summary>Adds or replaces one addon link and writes the file back.</summary>
    public void Set(string projectDirectory, string name, string localPath) {
        var resolved = _fileSystem.GetFullPath(localPath);

        if (!_fileSystem.DirectoryExists(resolved)) {
            throw new LocalLinkException(
                $"'{resolved}' is not a directory. A link points at the addon folder itself — the "
                + "one whose contents should appear under addons/ — not at the repository root "
                + "unless they are the same.");
        }

        var current = ReadAll(projectDirectory);
        var addons = new Dictionary<string, LocalLink>(current.Addons, StringComparer.Ordinal) {
            [name] = new LocalLink(name, resolved),
        };

        Write(projectDirectory, current with { Addons = addons });
    }

    /// <summary>
    /// Adds or replaces one dotnet link. <paramref name="localPath"/> may be a folder holding
    /// exactly one <c>.csproj</c> or the <c>.csproj</c> itself; either way the stored value is the
    /// project file, so nothing downstream has to re-derive it.
    /// </summary>
    public void SetDotnet(string projectDirectory, string packageId, string localPath,
        bool analyzer) {
        RefuseIfBuiltFromSource(projectDirectory, packageId);

        var resolved = _fileSystem.GetFullPath(localPath);

        string projectFile;
        if (resolved.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
            projectFile = _fileSystem.FileExists(resolved)
                ? resolved
                : throw new LocalLinkException($"'{resolved}' does not exist.");
        } else if (_fileSystem.DirectoryExists(resolved)) {
            try {
                // The same "exactly one .csproj, or say so" rule the consuming project already
                // uses, rather than a second notion of how to find a project.
                projectFile = CsprojLocator.Locate(_fileSystem, resolved);
            } catch (DotnetSyncException ex) {
                throw new LocalLinkException(ex.Message, ex);
            }
        } else {
            throw new LocalLinkException(
                $"'{resolved}' is neither a .csproj nor a folder containing one.");
        }

        var current = ReadAll(projectDirectory);
        var dotnet = new Dictionary<string, DotnetLink>(current.Dotnet, StringComparer.Ordinal) {
            [packageId] = new DotnetLink(packageId, projectFile, analyzer),
        };

        Write(projectDirectory, current with { Dotnet = dotnet });
    }

    /// <summary>
    /// Refuses a link for a package the project's <c>.csproj</c> already builds from source
    /// (docs/DESIGN-v0.26-SOURCE-BUILT-PACKAGES.md §E, decision 3).
    /// </summary>
    /// <remarks>
    /// The overlay works by removing the package and adding a <c>ProjectReference</c>. Beside a
    /// committed <c>ProjectReference</c> to the same package that is a second one, possibly to a
    /// different checkout, and MSBuild then builds whichever it settles on. Conditional or not: a
    /// conditional source reference still builds from source in its configuration, and the overlay
    /// would duplicate it there. Checked before anything about the local path, because no path
    /// makes this link safe.
    /// </remarks>
    private void RefuseIfBuiltFromSource(string projectDirectory, string packageId) {
        string csprojPath;
        try {
            csprojPath = CsprojLocator.Locate(_fileSystem, projectDirectory);
        } catch (DotnetSyncException) {
            // No .csproj (or several): nothing to collide with here, and the install that follows
            // says so.
            return;
        }

        var existing = SourceProvidedPackages.ReferencesTo(_fileSystem, csprojPath, packageId)
            .FirstOrDefault();
        if (existing is null) {
            return;
        }

        var when = existing.IsUnconditional ? string.Empty : $" when {existing.Condition}";
        throw new LocalLinkException(
            $"'{packageId}' is already built from source by {existing.Include} in "
            + $"{csprojPath}{when}. A local "
            + "link would add a second ProjectReference to the same package. Point that "
            + "ProjectReference at the checkout you want to build instead, or remove it before "
            + "linking.");
    }
    /// <summary>Removes one addon link. Returns false when there was nothing to remove, so a caller
    /// can say so rather than reporting a change that didn't happen.</summary>
    public bool Remove(string projectDirectory, string name) {
        var current = ReadAll(projectDirectory);
        var addons = new Dictionary<string, LocalLink>(current.Addons, StringComparer.Ordinal);

        if (!addons.Remove(name)) {
            return false;
        }

        Write(projectDirectory, current with { Addons = addons });
        return true;
    }

    /// <summary>Removes one dotnet link.</summary>
    public bool RemoveDotnet(string projectDirectory, string packageId) {
        var current = ReadAll(projectDirectory);
        var dotnet = new Dictionary<string, DotnetLink>(current.Dotnet, StringComparer.Ordinal);

        if (!dotnet.Remove(packageId)) {
            return false;
        }

        Write(projectDirectory, current with { Dotnet = dotnet });
        return true;
    }

    private void Write(string projectDirectory, LocalLinkSet links) {
        var path = PathFor(projectDirectory);

        if (links.IsEmpty) {
            // An empty file is indistinguishable from no links, and leaving one behind means the
            // next person to open the project finds a mysterious empty file suggesting something is
            // configured when nothing is.
            _fileSystem.DeleteFile(path);
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Dotquay local links — per-machine, NOT for committing (SPEC.md §7.4).");
        sb.AppendLine("# [link.addon.*] replaces an addon's install with a directory link into a "
            + "folder on this");
        sb.AppendLine("# machine. [link.dotnet.*] swaps a NuGet package for a local .csproj via "
            + "the generated");
        sb.AppendLine($"# {LocalTargetsWriter.FileName}, which is also per-machine.");
        sb.AppendLine($"# Add {FileName} and {LocalTargetsWriter.FileName} to .gitignore.");

        foreach (var (name, link) in links.Addons.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            sb.AppendLine();
            sb.AppendLine($"[{SectionKey}.{AddonSection}.{FormatKey(name)}]");
            sb.AppendLine($"path = {Quote(link.Path)}");
        }

        foreach (var (id, link) in links.Dotnet.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            sb.AppendLine();
            sb.AppendLine($"[{SectionKey}.{DotnetSection}.{FormatKey(id)}]");
            sb.AppendLine($"path = {Quote(link.ProjectPath)}");

            if (link.Analyzer) {
                sb.AppendLine("analyzer = true");
            }
        }

        _fileSystem.WriteAllText(path, sb.ToString());
    }

    /// <summary>Quotes anything that isn't a bare TOML key — which every dotted NuGet package ID
    /// needs, or <c>Kani.Core</c> would be written as two nested tables and read back as a package
    /// called <c>Core</c> under one called <c>Kani</c>.</summary>
    private static string FormatKey(string key) =>
        key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? key : Quote(key);

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <summary>Thrown for a malformed or unusable <c>dotquay.local.toml</c>, or a link target that
/// can't be used. A file that can't be read at all is the narrower
/// <see cref="LocalLinksParseException"/>.</summary>
public class LocalLinkException : Exception {
    public LocalLinkException(string message)
        : base(message) {
    }

    public LocalLinkException(string message, Exception innerException)
        : base(message, innerException) {
    }
}
