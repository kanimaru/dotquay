using Tomlyn;
using Tomlyn.Model;

namespace Dotquay.Core.Manifest;

/// <summary>Parses <c>dotquay.toml</c> text (project or package form) into the models in this
/// namespace. Validation lives here rather than in the models themselves, so the models stay
/// plain data and every error path is exercised by <c>ManifestReaderTests</c>.</summary>
public static class ManifestReader
{
    public static ProjectManifest ReadProject(string text, string sourcePath = "dotquay.toml")
    {
        var table = Parse(text, sourcePath);

        string? godot = null;
        string? nuGetSource = null;
        if (table.TryGetValue("project", out var projectObj) && projectObj is TomlTable projectTable)
        {
            godot = GetString(projectTable, "godot");
            nuGetSource = GetString(projectTable, "nuget_source");
        }

        return new ProjectManifest
        {
            Godot = godot,
            NuGetSource = nuGetSource,
            Addons = ReadAddonTable(table, "addons", sourcePath),
            Dotnet = ReadDotnetTable(table, "dotnet", sourcePath),
            Overrides = ReadAddonTable(table, "overrides", sourcePath),
        };
    }

    public static PackageManifest ReadPackage(string text, string sourcePath = "dotquay.toml")
    {
        var table = Parse(text, sourcePath);

        if (!table.TryGetValue("package", out var packageObj) || packageObj is not TomlTable packageTable)
        {
            throw new ManifestParseException(
                $"{sourcePath}: package manifest is missing the [package] header (SPEC.md §7.2).");
        }

        var name = GetString(packageTable, "name")
            ?? throw new ManifestParseException($"{sourcePath}: [package] is missing required key 'name'.");
        var version = GetString(packageTable, "version")
            ?? throw new ManifestParseException($"{sourcePath}: [package] is missing required key 'version'.");

        return new PackageManifest
        {
            Name = name,
            Version = version,
            Addons = ReadAddonTable(table, "addons", sourcePath),
            Dotnet = ReadDotnetTable(table, "dotnet", sourcePath),
        };
    }

    private static TomlTable Parse(string text, string sourcePath)
    {
        var options = new TomlSerializerOptions { SourceName = sourcePath };
        try
        {
            return TomlSerializer.Deserialize<TomlTable>(text, options) ?? new TomlTable();
        }
        catch (TomlException ex)
        {
            throw new ManifestParseException($"{sourcePath}: invalid TOML.{Environment.NewLine}{ex.Message}", ex);
        }
    }

    private static Dictionary<string, AddonSpec> ReadAddonTable(TomlTable root, string sectionKey, string sourcePath)
    {
        var result = new Dictionary<string, AddonSpec>();

        if (!root.TryGetValue(sectionKey, out var raw) || raw is not TomlTable table)
        {
            return result;
        }

        foreach (var name in table.Keys)
        {
            if (table[name] is not TomlTable entry)
            {
                throw new ManifestParseException($"{sourcePath}: [{sectionKey}.{name}] must be a table.");
            }

            var git = GetString(entry, "git")
                ?? throw new ManifestParseException($"{sourcePath}: [{sectionKey}.{name}] is missing required key 'git'.");
            var version = GetString(entry, "version");
            var branch = GetString(entry, "branch");
            var rev = GetString(entry, "rev");
            var path = GetString(entry, "path");
            var installAs = GetString(entry, "install_as");

            var sourceCount = new[] { version, branch, rev }.Count(v => v is not null);
            if (sourceCount != 1)
            {
                throw new ManifestParseException(
                    $"{sourcePath}: [{sectionKey}.{name}] must set exactly one of 'version', 'branch', or "
                    + $"'rev' (SPEC.md §7.1); found {sourceCount}.");
            }

            if (installAs is not null && !IsSafeFolderName(installAs))
            {
                throw new ManifestParseException(
                    $"{sourcePath}: [{sectionKey}.{name}] has install_as = \"{installAs}\", which is not a plain "
                    + "folder name. It must be a single directory name — no slashes, no '..', no drive letters — "
                    + "because dotquay deletes and replaces that folder wholesale during install (SPEC.md §9).");
            }

            result[name] = new AddonSpec
            {
                Name = name,
                Git = git,
                Version = version,
                Branch = branch,
                Rev = rev,
                Path = path,
                InstallAs = installAs,
            };
        }

        return result;
    }

    /// <summary>
    /// <c>[dotnet.&lt;PackageId&gt;]</c> headers use TOML's dotted-table syntax, so a package like
    /// <c>Newtonsoft.Json</c> parses as three levels of nested tables
    /// (<c>dotnet -&gt; Newtonsoft -&gt; Json</c>), not one table named "Newtonsoft.Json". This walks
    /// the tree and treats any table carrying a string <c>version</c> key as a leaf package spec,
    /// reassembling the dotted package ID from the path taken to reach it.
    /// </summary>
    private static Dictionary<string, DotnetSpec> ReadDotnetTable(TomlTable root, string sectionKey, string sourcePath)
    {
        var result = new Dictionary<string, DotnetSpec>();

        if (!root.TryGetValue(sectionKey, out var raw) || raw is not TomlTable table)
        {
            return result;
        }

        CollectDotnetSpecs(table, path: null, sectionKey, sourcePath, result);
        return result;
    }

    private static void CollectDotnetSpecs(
        TomlTable table,
        string? path,
        string sectionKey,
        string sourcePath,
        Dictionary<string, DotnetSpec> result)
    {
        foreach (var key in table.Keys)
        {
            var childPath = path is null ? key : $"{path}.{key}";
            var value = table[key];

            if (value is TomlTable childTable)
            {
                if (childTable.TryGetValue("version", out var versionObj) && versionObj is string version)
                {
                    result[childPath] = new DotnetSpec { Name = childPath, Version = version };
                }

                CollectDotnetSpecs(childTable, childPath, sectionKey, sourcePath, result);
            }
            else if (key != "version")
            {
                throw new ManifestParseException(
                    $"{sourcePath}: [{sectionKey}] entry '{childPath}' is not a table — dotnet dependencies "
                    + "must be declared as [dotnet.<PackageId>] with a 'version' key.");
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is a single, ordinary directory name.
    /// </summary>
    /// <remarks>
    /// This is a safety boundary, not tidiness. The sync engine deletes and replaces the destination
    /// folder wholesale (SPEC.md §9 step 5), and <c>install_as</c> is the one field that chooses it —
    /// so a value like <c>"../.."</c>, <c>"/etc"</c>, or <c>"C:\Windows"</c> would aim that deletion
    /// outside the project. Manifests travel with third-party addons and are read from repositories
    /// nobody in the dependency chain necessarily audited, so this is checked at parse time, before
    /// the value can reach a path operation.
    /// </remarks>
    private static bool IsSafeFolderName(string folder) =>
        !string.IsNullOrWhiteSpace(folder)
        && folder is not ("." or "..")
        && folder.IndexOfAny(['/', '\\', ':']) < 0
        && folder.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;

    private static string? GetString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string s ? s : null;
}
