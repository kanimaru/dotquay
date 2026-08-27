using System.Xml.Linq;
using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Dotnet;

/// <summary>
/// Adds, updates, and removes <c>&lt;PackageReference&gt;</c> elements in a <c>.csproj</c> (SPEC.md
/// §11: "writes PackageReference entries into the project's .csproj"). Uses <c>System.Xml.Linq</c>
/// directly rather than MSBuild's own object model — dotquay only ever needs to add/find/remove one
/// element kind, and pulling in a full MSBuild dependency for that would be a lot of AOT/trim risk
/// for very little benefit. Like <see cref="Manifest.ManifestWriter"/>, this round-trips through a
/// parsed tree rather than preserving the original file byte-for-byte, so incidental whitespace can
/// shift; actual content (other items, properties, comments) survives.
/// </summary>
public static class CsprojPackageReferenceWriter
{
    /// <summary>Adds a new <c>PackageReference</c>, or updates the <c>Version</c> attribute of an
    /// existing one (matched case-insensitively on <c>Include</c>, since NuGet package IDs are
    /// themselves case-insensitive).</summary>
    public static void AddOrUpdate(IFileSystem fileSystem, string csprojPath, string packageId, string version)
    {
        var (document, project) = Load(fileSystem, csprojPath);

        var existing = FindReference(project, packageId);
        if (existing is not null)
        {
            existing.SetAttributeValue("Version", version);
        }
        else
        {
            var itemGroup = project.Elements("ItemGroup").FirstOrDefault(g => g.Elements("PackageReference").Any());
            if (itemGroup is null)
            {
                itemGroup = new XElement("ItemGroup");
                project.Add(itemGroup);
            }

            itemGroup.Add(new XElement("PackageReference", new XAttribute("Include", packageId), new XAttribute("Version", version)));
        }

        Save(fileSystem, csprojPath, document);
    }

    /// <summary>Removes a <c>PackageReference</c> if present. Returns whether one was found and
    /// removed, so callers can tell "already gone" from "just removed" without a second read.</summary>
    public static bool Remove(IFileSystem fileSystem, string csprojPath, string packageId)
    {
        var (document, project) = Load(fileSystem, csprojPath);

        var existing = FindReference(project, packageId);
        if (existing is null)
        {
            return false;
        }

        existing.Remove();
        Save(fileSystem, csprojPath, document);
        return true;
    }

    private static XElement? FindReference(XElement project, string packageId) =>
        project.Descendants("PackageReference")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));

    private static (XDocument Document, XElement Project) Load(IFileSystem fileSystem, string csprojPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(fileSystem.ReadAllText(csprojPath));
        }
        catch (System.Xml.XmlException ex)
        {
            throw new DotnetSyncException($"{csprojPath}: not valid XML.{Environment.NewLine}{ex.Message}", ex);
        }

        var project = document.Root;
        if (project is null || project.Name.LocalName != "Project")
        {
            throw new DotnetSyncException($"{csprojPath}: expected a <Project> root element.");
        }

        return (document, project);
    }

    private static void Save(IFileSystem fileSystem, string csprojPath, XDocument document) =>
        fileSystem.WriteAllText(csprojPath, document.ToString(SaveOptions.None) + Environment.NewLine);
}
