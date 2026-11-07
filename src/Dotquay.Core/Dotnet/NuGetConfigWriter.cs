using System.Xml.Linq;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Dotnet;

/// <summary>
/// Makes the project's <c>NuGet.Config</c> declare the feeds from <c>dotquay.toml</c>, so
/// <c>dotnet restore</c> — a separate process reading its own config, entirely unaware of dotquay's
/// own <see cref="NuGetV3Client"/> queries — actually knows to look there (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// <para>Touches only what it owns: the <c>dotquay-*</c> keys (and the grandfathered bare
/// <c>dotquay</c> one). Every other source, and the rest of the file, is left exactly as found —
/// same rule as <see cref="CsprojPackageReferenceWriter"/>.</para>
/// <para>Writes URLs only, never credentials. This file is meant to be committed, so authentication
/// is threaded through as <c>NuGetPackageSourceCredentials_*</c> environment variables on the
/// <c>dotnet restore</c> child process instead (see <see cref="DotnetInstaller"/>), which NuGet
/// resolves at restore time without anything touching disk. Confirmed against Microsoft's own docs
/// (learn.microsoft.com/en-us/nuget/consume-packages/consuming-packages-authenticated-feeds).
/// </para>
/// </remarks>
public static class NuGetConfigWriter {
    /// <summary>The key the pre-v0.7 single-source form used, preserved for the source named
    /// <see cref="NuGetSourceSpec.LegacyName"/> so upgrading doesn't rewrite existing
    /// files.</summary>
    public const string DotquaySourceName = "dotquay";

    private const string ConfigFileName = "NuGet.Config";
    private const string LowercaseConfigFileName = "nuget.config";

    /// <summary>
    /// Creates <c>NuGet.Config</c> if none exists, or updates the existing one — either casing is
    /// accepted, since NuGet itself doesn't care and dotquay shouldn't create a second file next to
    /// one that's already there.
    /// </summary>
    /// <param name="pinnedPackages">Package ID to source name, from <c>[dotnet.*] source =
    /// "..."</c>. Drives <c>&lt;packageSourceMapping&gt;</c>; empty still emits the catch-all
    /// mapping when <paramref name="writeSourceMapping"/> is set.</param>
    /// <param name="writeSourceMapping">The project's <c>[nuget] source_mapping</c>.</param>
    public static void EnsureSources(
        IFileSystem fileSystem,
        string projectDirectory,
        NuGetSourceSet sources,
        IReadOnlyDictionary<string, string>? pinnedPackages = null,
        bool writeSourceMapping = true) {
        var path = ResolvePath(fileSystem, projectDirectory);
        var document = LoadOrCreate(fileSystem, path);
        var configuration = document.Root;
        if (configuration is null || configuration.Name.LocalName != "configuration") {
            throw new DotnetSyncException($"{path}: expected a <configuration> root element.");
        }

        var declared = sources.Declared.Where(source => !source.IsPublicDefault).ToList();

        WritePackageSources(configuration, declared, writeSourceMapping);

        if (writeSourceMapping && declared.Count > 0) {
            WritePackageSourceMapping(configuration, declared, pinnedPackages
                ?? new Dictionary<string, string>());
        }

        fileSystem.WriteAllText(path, document.ToString(SaveOptions.None) + Environment.NewLine);
    }

    private static XDocument LoadOrCreate(IFileSystem fileSystem, string path) {
        if (!fileSystem.FileExists(path)) {
            return new XDocument(new XElement("configuration"));
        }

        try {
            return XDocument.Parse(fileSystem.ReadAllText(path));
        } catch (System.Xml.XmlException ex) {
            throw new DotnetSyncException($"{path}: not valid "
                + $"XML.{Environment.NewLine}{ex.Message}", ex);
        }
    }

    /// <summary>
    /// The <c>&lt;packageSourceMapping&gt;</c> the project's config currently declares: each source
    /// key to the patterns bound to it.
    /// </summary>
    /// <remarks>
    /// The read half of <see cref="WritePackageSourceMapping"/>, kept here so this class stays the
    /// one place that knows the file's shape. Deliberately forgiving: a missing file, a missing
    /// mapping element and a file too broken to parse all read as "nothing is bound", because the
    /// only caller is <c>doctor</c>, which reports rather than writes - and an unparseable file is
    /// already reported by anything that tries to write it.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSourceMapping(
        IFileSystem fileSystem, string projectDirectory) {
        var mapped = new Dictionary<string,
            IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var path = ResolvePath(fileSystem, projectDirectory);

        if (!fileSystem.FileExists(path)) {
            return mapped;
        }

        XDocument document;
        try {
            document = XDocument.Parse(fileSystem.ReadAllText(path));
        } catch (System.Xml.XmlException) {
            return mapped;
        }

        var mapping = document.Root?.Element("packageSourceMapping");
        if (mapping is null) {
            return mapped;
        }

        foreach (var source in mapping.Elements("packageSource")) {
            if ((string?)source.Attribute("key") is not { } key) {
                continue;
            }

            mapped[key] = source.Elements("package")
                .Select(package => (string?)package.Attribute("pattern"))
                .OfType<string>()
                .ToList();
        }

        return mapped;
    }

    /// <summary>Either casing, preferring one that is already on disk - NuGet does not care which,
    /// and dotquay must not create a second file beside one that is already there.</summary>
    private static string ResolvePath(IFileSystem fileSystem, string projectDirectory) {
        var lowercasePath = fileSystem.CombinePath(projectDirectory, LowercaseConfigFileName);

        return fileSystem.FileExists(lowercasePath)
            ? lowercasePath
            : fileSystem.CombinePath(projectDirectory, ConfigFileName);
    }

    /// <summary>
    /// Syncs the <c>&lt;packageSources&gt;</c> entries dotquay owns, adding, updating, and
    /// removing.
    /// </summary>
    /// <remarks>
    /// Pruning matters as much as adding: a feed deleted from <c>dotquay.toml</c> that stayed in
    /// <c>NuGet.Config</c> would keep being searched by <c>dotnet restore</c> forever, quietly
    /// undoing the removal.
    /// </remarks>
    private static void WritePackageSources(
        XElement configuration, IReadOnlyList<NuGetSourceSpec> declared, bool writeSourceMapping) {
        var packageSources = configuration.Element("packageSources");
        if (packageSources is null) {
            packageSources = new XElement("packageSources");
            configuration.Add(packageSources);
        }

        var wanted = declared.ToDictionary(source => source.ConfigKey, source => source.Url,
            StringComparer.OrdinalIgnoreCase);

        // A mapping element makes NuGet ignore every source that has no pattern, so the catch-all
        // needs a nuget.org entry to point at. It cannot be inherited from machine-level config: a
        // mapping key must name a source declared in the same file.
        if (writeSourceMapping && declared.Count > 0) {
            wanted[NuGetSourceSpec.PublicName] = NuGetSourceSpec.PublicUrl;
        }

        foreach (var stale in packageSources.Elements("add")
                     .Where(element => IsOwnedKey((string?)element.Attribute("key"))
                                       && !wanted.ContainsKey((string?)element.Attribute("key")
                                           ?? string.Empty))
                     .ToList()) {
            stale.Remove();
        }

        foreach (var (key, url) in wanted) {
            var existing = packageSources.Elements("add")
                .FirstOrDefault(element =>
                    string.Equals((string?)element.Attribute("key"), key,
                        StringComparison.OrdinalIgnoreCase));

            if (existing is not null) {
                existing.SetAttributeValue("value", url);
            } else {
                packageSources.Add(new XElement("add", new XAttribute("key", key),
                    new XAttribute("value", url)));
            }
        }
    }

    /// <summary>
    /// Binds each pinned package to the feed dotquay resolved it from, so restore can't pick a
    /// different one.
    /// </summary>
    /// <remarks>
    /// Without this, multi-feed support is a report rather than a guarantee — dotquay would say
    /// "resolved from core" while restore independently searched every source and could take a
    /// same-named package from nuget.org. The <c>*</c> catch-all is mandatory, not a nicety:
    /// <c>packageSourceMapping</c> is all-or-nothing per config, and any package matching no
    /// pattern fails restore outright with <c>NU1100</c>.
    /// </remarks>
    private static void WritePackageSourceMapping(
        XElement configuration,
        IReadOnlyList<NuGetSourceSpec> declared,
        IReadOnlyDictionary<string, string> pinnedPackages) {
        configuration.Element("packageSourceMapping")?.Remove();

        var mapping = new XElement("packageSourceMapping");

        foreach (var source in declared) {
            var patterns = pinnedPackages
                .Where(pin => string.Equals(pin.Value, source.Name, StringComparison.Ordinal))
                .Select(pin => pin.Key)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            if (patterns.Count == 0) {
                continue;
            }

            mapping.Add(new XElement(
                "packageSource",
                new XAttribute("key", source.ConfigKey),
                patterns.Select(pattern =>
                    new XElement("package", new XAttribute("pattern", pattern)))));
        }

        mapping.Add(new XElement(
            "packageSource",
            new XAttribute("key", NuGetSourceSpec.PublicName),
            new XElement("package", new XAttribute("pattern", "*"))));

        configuration.Add(mapping);
    }

    /// <summary>Whether dotquay wrote this key and may therefore remove it. Deliberately narrow —
    /// anything else in the file belongs to the project.</summary>
    private static bool IsOwnedKey(string? key) =>
        key is not null
        && (string.Equals(key, DotquaySourceName, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(NuGetSourceSpec.ConfigKeyPrefix + "-",
                StringComparison.OrdinalIgnoreCase));
}
