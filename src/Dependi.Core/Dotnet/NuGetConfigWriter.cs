using System.Xml.Linq;
using Dependi.Core.Abstractions;

namespace Dependi.Core.Dotnet;

/// <summary>
/// Ensures a project's <c>NuGet.Config</c> declares the manifest's configured private
/// <c>nuget_source</c> as a package source, so <c>dotnet restore</c> — a separate process reading its
/// own config, entirely unaware of dependi's own <see cref="NuGetV3Client"/> queries — actually knows
/// to look there (SPEC.md §11). Only ever adds/updates the one <c>"dependi"</c>-keyed entry; every
/// other source, and the rest of the file, is left exactly as found, same "touch only what you own"
/// spirit as <see cref="CsprojPackageReferenceWriter"/>.
/// </summary>
/// <remarks>
/// Deliberately writes only the source URL, never credentials — this file is meant to be committed
/// (SPEC.md §11's "credentials never in dependi.toml" rule applies here too). Authentication is
/// instead threaded through as the <c>NuGetPackageSourceCredentials_dependi</c> environment variable
/// on the <c>dotnet restore</c> child process itself (see <see cref="DotnetInstaller"/>), which NuGet
/// resolves at restore time without ever touching disk. Confirmed against Microsoft's own docs
/// (learn.microsoft.com/en-us/nuget/consume-packages/consuming-packages-authenticated-feeds).
/// </remarks>
public static class NuGetConfigWriter
{
    /// <summary>Fixed key for dependi's own source entry — v0.1 supports exactly one custom
    /// <c>nuget_source</c> per project, so there's no need to name it after the URL.</summary>
    public const string DependiSourceName = "dependi";

    private const string ConfigFileName = "NuGet.Config";
    private const string LowercaseConfigFileName = "nuget.config";

    /// <summary>Creates <c>NuGet.Config</c> if none exists, or updates the existing one — either
    /// casing is accepted, since NuGet itself doesn't care and dependi shouldn't create a second file
    /// next to one that's already there.</summary>
    public static void EnsureSource(IFileSystem fileSystem, string projectDirectory, string sourceUrl)
    {
        var lowercasePath = fileSystem.CombinePath(projectDirectory, LowercaseConfigFileName);
        var path = fileSystem.FileExists(lowercasePath)
            ? lowercasePath
            : fileSystem.CombinePath(projectDirectory, ConfigFileName);

        XDocument document;
        if (fileSystem.FileExists(path))
        {
            try
            {
                document = XDocument.Parse(fileSystem.ReadAllText(path));
            }
            catch (System.Xml.XmlException ex)
            {
                throw new DotnetSyncException($"{path}: not valid XML.{Environment.NewLine}{ex.Message}", ex);
            }
        }
        else
        {
            document = new XDocument(new XElement("configuration"));
        }

        var configuration = document.Root;
        if (configuration is null || configuration.Name.LocalName != "configuration")
        {
            throw new DotnetSyncException($"{path}: expected a <configuration> root element.");
        }

        var packageSources = configuration.Element("packageSources");
        if (packageSources is null)
        {
            packageSources = new XElement("packageSources");
            configuration.Add(packageSources);
        }

        var existing = packageSources.Elements("add")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("key"), DependiSourceName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.SetAttributeValue("value", sourceUrl);
        }
        else
        {
            packageSources.Add(new XElement("add", new XAttribute("key", DependiSourceName), new XAttribute("value", sourceUrl)));
        }

        fileSystem.WriteAllText(path, document.ToString(SaveOptions.None) + Environment.NewLine);
    }
}
