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

        string? legacyNuGetSource = null;
        string? patchesDirectory = null;
        if (table.TryGetValue("project", out var projectObj) && projectObj is TomlTable projectTable)
        {
            patchesDirectory = GetString(projectTable, "patches_dir");

            // A `godot` key here is read and discarded on purpose. It existed through v0.5, nothing
            // consumed it, and it has been removed — but manifests in the wild still carry it, and
            // an unknown key must not be an error for a file people hand-edit.
            legacyNuGetSource = GetString(projectTable, "nuget_source");
        }

        var nuget = ReadNuGetSection(table, legacyNuGetSource, sourcePath);
        var sources = nuget.Sources;
        var dotnet = ReadDotnetTable(table, "dotnet", sourcePath);
        ValidateSourcePins(dotnet, sources, sourcePath);

        return new ProjectManifest
        {
            NuGetSources = sources,
            NuGetSourceMapping = nuget.SourceMapping,
            TrustedHosts = nuget.TrustedHosts,
            PatchesDirectory = patchesDirectory,
            Addons = ReadAddonTable(table, "addons", sourcePath),
            Dotnet = dotnet,
            Overrides = ReadAddonTable(table, "overrides", sourcePath),
        };
    }

    /// <summary>
    /// Reads <c>[nuget]</c> and its <c>[nuget.sources.*]</c> sub-tables, folding in a legacy
    /// <c>[project] nuget_source</c> (SPEC.md §7.1).
    /// </summary>
    /// <remarks>
    /// Declaration order is preserved because it is the resolution rule (SPEC.md §11.1) — Tomlyn's
    /// <see cref="TomlTable"/> enumerates in file order, and this walks it rather than building a
    /// dictionary whose enumeration order is unspecified.
    /// </remarks>
    private sealed record NuGetSection(
        List<NuGetSourceSpec> Sources, bool SourceMapping, IReadOnlyList<string> TrustedHosts);

    private static NuGetSection ReadNuGetSection(
        TomlTable root, string? legacyNuGetSource, string sourcePath, string? declaredByAddon = null)
    {
        var sources = new List<NuGetSourceSpec>();
        var sourceMapping = true;
        IReadOnlyList<string> trustedHosts = [];

        if (legacyNuGetSource is not null)
        {
            sources.Add(new NuGetSourceSpec(NuGetSourceSpec.LegacyName, legacyNuGetSource));
        }

        if (!root.TryGetValue("nuget", out var raw) || raw is not TomlTable nuget)
        {
            return new NuGetSection(sources, sourceMapping, trustedHosts);
        }

        if (nuget.TryGetValue("source_mapping", out var mappingValue))
        {
            sourceMapping = mappingValue is bool enabled
                ? enabled
                : throw new ManifestParseException($"{sourcePath}: [nuget] source_mapping must be true or false.");
        }

        if (nuget.TryGetValue("trusted_hosts", out var trustedValue))
        {
            trustedHosts = ReadStringArray(trustedValue, sourcePath, "[nuget] trusted_hosts");
        }

        if (!nuget.TryGetValue("sources", out var rawSources) || rawSources is not TomlTable sourcesTable)
        {
            return new NuGetSection(sources, sourceMapping, trustedHosts);
        }

        foreach (var name in sourcesTable.Keys)
        {
            if (sourcesTable[name] is not TomlTable entry)
            {
                throw new ManifestParseException(
                    $"{sourcePath}: [nuget.sources.{name}] must be a table with a 'url' key.");
            }

            if (!NuGetSourceSpec.IsValidName(name))
            {
                throw new ManifestParseException(
                    $"{sourcePath}: '{name}' is not a valid NuGet source name. Use letters, digits, "
                    + "'.', '-' or '_', starting with a letter or digit — the name becomes part of an "
                    + "environment variable name and a NuGet.Config key.");
            }

            var url = GetString(entry, "url")
                ?? throw new ManifestParseException($"{sourcePath}: [nuget.sources.{name}] is missing required key 'url'.");

            // Both forms mean the same thing, and disagreeing about the URL has no correct answer —
            // so this is an error rather than a silent precedence rule nobody would remember.
            if (sources.Any(existing => string.Equals(existing.Name, name, StringComparison.Ordinal)))
            {
                throw new ManifestParseException(
                    name == NuGetSourceSpec.LegacyName
                        ? $"{sourcePath}: [project] nuget_source and [nuget.sources.{NuGetSourceSpec.LegacyName}] "
                          + "both declare the same source. Keep one — the [nuget.sources.*] form is current."
                        : $"{sourcePath}: NuGet source '{name}' is declared twice.");
            }

            sources.Add(new NuGetSourceSpec(name, url)
            {
                Packages = entry.TryGetValue("packages", out var packagesValue)
                    ? ReadStringArray(packagesValue, sourcePath, $"[nuget.sources.{name}] packages")
                    : [],
                // In an addon's own manifest the addon names itself; in a project manifest the key
                // records which addon a block was adopted from, so a rewrite keeps the attribution.
                DeclaredByAddon = declaredByAddon ?? GetString(entry, "declared_by"),
            });
        }

        return new NuGetSection(sources, sourceMapping, trustedHosts);
    }

    /// <summary>Reads a TOML array of strings, rejecting anything else by name rather than letting a
    /// mistyped value silently become an empty list — a silently-empty <c>trusted_hosts</c> would
    /// turn a security control off without saying so.</summary>
    private static IReadOnlyList<string> ReadStringArray(object value, string sourcePath, string what)
    {
        if (value is not TomlArray array)
        {
            throw new ManifestParseException($"{sourcePath}: {what} must be an array of strings.");
        }

        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not string text || text.Length == 0)
            {
                throw new ManifestParseException($"{sourcePath}: {what} must contain only non-empty strings.");
            }

            result.Add(text);
        }

        return result;
    }

    /// <summary>Checks every <c>source = "..."</c> pin names a source that exists, at parse time —
    /// so a typo is reported against the manifest that contains it rather than as a package that
    /// mysteriously can't be found.</summary>
    private static void ValidateSourcePins(
        Dictionary<string, DotnetSpec> dotnet, List<NuGetSourceSpec> sources, string sourcePath)
    {
        foreach (var spec in dotnet.Values)
        {
            if (spec.Source is null
                || sources.Any(source => string.Equals(source.Name, spec.Source, StringComparison.Ordinal))
                || string.Equals(spec.Source, NuGetSourceSpec.PublicName, StringComparison.Ordinal))
            {
                continue;
            }

            var known = sources.Count == 0
                ? $"'{NuGetSourceSpec.PublicName}'"
                : string.Join(", ", sources.Select(source => $"'{source.Name}'").Append($"'{NuGetSourceSpec.PublicName}'"));

            throw new ManifestParseException(
                $"{sourcePath}: [dotnet.{spec.Name}] has source = \"{spec.Source}\", which isn't a declared "
                + $"NuGet source. Declared: {known}.");
        }
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

        // An addon may declare feeds, but nothing else in [nuget]: source_mapping and trusted_hosts
        // are the consuming project's decisions, and an addon that could set them would be deciding
        // its consumer's security policy (SPEC.md §11.3). Read and discarded rather than rejected,
        // since an addon repo is itself a valid dotquay project and may legitimately carry both.
        var nuget = ReadNuGetSection(table, legacyNuGetSource: null, sourcePath, declaredByAddon: name);

        // `version` is optional: nothing reads it, the git tag decides resolution, and plugin.cfg is
        // what doctor checks for drift. Requiring it only added a third place to bump.
        return new PackageManifest
        {
            Name = name,
            Version = GetString(packageTable, "version"),
            Addons = ReadAddonTable(table, "addons", sourcePath),
            Dotnet = ReadDotnetTable(table, "dotnet", sourcePath),
            NuGetSources = nuget.Sources,
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
                    // A string `source` here is a feed pin; a *table* named `source` is a package ID
                    // segment and is handled by the recursion below. TOML can't have both under one
                    // key, so the two readings never collide.
                    var pinned = childTable.TryGetValue("source", out var sourceObj) && sourceObj is string pin
                        ? pin
                        : null;

                    result[childPath] = new DotnetSpec
                    {
                        Name = childPath,
                        Version = version,
                        Source = pinned,
                        Assets = ReadAssets(childTable, sectionKey, childPath, sourcePath),
                    };
                }

                CollectDotnetSpecs(childTable, childPath, sectionKey, sourcePath, result);
            }
            else if (key is not ("version" or "source" or AssetKeys.Include or AssetKeys.Exclude or AssetKeys.Private))
            {
                throw new ManifestParseException(
                    $"{sourcePath}: [{sectionKey}] entry '{childPath}' is not a table — dotnet dependencies "
                    + "must be declared as [dotnet.<PackageId>] with a 'version' key.");
            }
        }
    }

    /// <summary>The TOML key names for asset metadata, shared with <see cref="ManifestWriter"/> and
    /// with the whitelist in <see cref="CollectDotnetSpecs"/> — three spellings of each would be three
    /// chances to disagree.</summary>
    internal static class AssetKeys
    {
        public const string Include = "include_assets";
        public const string Exclude = "exclude_assets";
        public const string Private = "private_assets";
    }

    /// <summary>
    /// Reads the <c>include_assets</c>/<c>exclude_assets</c>/<c>private_assets</c> arrays off one
    /// <c>[dotnet.&lt;PackageId&gt;]</c> table (v0.10).
    /// </summary>
    /// <remarks>
    /// Structural problems throw — a non-array, a non-string element, an empty list, or <c>all</c>/
    /// <c>none</c> mixed with other tags. An unrecognised <em>tag</em> does not: MSBuild may add tags
    /// after this code was written, and refusing one would make dotquay the reason a valid manifest
    /// cannot be used. Those are surfaced as warnings at install time instead
    /// (<see cref="PackageAssets.UnknownTags"/>).
    /// </remarks>
    private static PackageAssets ReadAssets(TomlTable table, string sectionKey, string packageId, string sourcePath) =>
        new()
        {
            Include = ReadAssetTags(table, AssetKeys.Include, sectionKey, packageId, sourcePath),
            Exclude = ReadAssetTags(table, AssetKeys.Exclude, sectionKey, packageId, sourcePath),
            Private = ReadAssetTags(table, AssetKeys.Private, sectionKey, packageId, sourcePath),
        };

    private static IReadOnlyList<string>? ReadAssetTags(
        TomlTable table, string key, string sectionKey, string packageId, string sourcePath)
    {
        if (!table.TryGetValue(key, out var value))
        {
            return null;
        }

        var what = $"[{sectionKey}.{packageId}] {key}";
        var tags = PackageAssets.Normalize(ReadStringArray(value, sourcePath, what));

        if (tags.Count == 0)
        {
            throw new ManifestParseException(
                $"{sourcePath}: {what} is empty. Remove the key to leave the .csproj alone, or use "
                + "[\"none\"] to say no assets.");
        }

        var misuse = PackageAssets.FindMisuseOfAllOrNone(tags);
        if (misuse is not null)
        {
            throw new ManifestParseException(
                $"{sourcePath}: {what} lists '{misuse}' alongside other values — '{misuse}' must "
                + "appear on its own.");
        }

        return tags;
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
    internal static bool IsSafeFolderName(string folder) =>
        !string.IsNullOrWhiteSpace(folder)
        && folder is not ("." or "..")
        && folder.IndexOfAny(['/', '\\', ':']) < 0
        && folder.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;

    private static string? GetString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string s ? s : null;
}
