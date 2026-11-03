using System.Xml.Linq;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Local;
using Dotquay.Core.Manifest;

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
/// <remarks>
/// Asset metadata (v0.10) follows a strict ownership rule, because the <c>.csproj</c> is a file the
/// user edits too: a list the manifest declares is dotquay's to write and replace; a list the
/// manifest is silent about is left exactly as found, <em>unless</em> dotquay is the one that wrote
/// it (which the lockfile records), in which case it is removed. Anything else on the element —
/// <c>Condition</c>, <c>NoWarn</c>, unrelated children — is never touched.
/// </remarks>
public static class CsprojPackageReferenceWriter
{
    /// <summary>The three MSBuild metadata names, paired with their slot on
    /// <see cref="PackageAssets"/>. One table so a new slot cannot be added to the model and
    /// forgotten here.</summary>
    private static readonly (string Element, Func<PackageAssets, IReadOnlyList<string>?> Get)[] AssetSlots =
    [
        ("IncludeAssets", assets => assets.Include),
        ("ExcludeAssets", assets => assets.Exclude),
        ("PrivateAssets", assets => assets.Private),
    ];

    /// <summary>Adds a new <c>PackageReference</c>, or updates the <c>Version</c> attribute of an
    /// existing one (matched case-insensitively on <c>Include</c>, since NuGet package IDs are
    /// themselves case-insensitive).</summary>
    /// <param name="declared">Asset metadata the manifest declares. Null lists mean "not declared".</param>
    /// <param name="previouslyWritten">Asset metadata dotquay wrote last time, from the lockfile. A
    /// list present here but absent from <paramref name="declared"/> is removed — that is the only way
    /// to tell a withdrawn declaration from a user's hand-edit.</param>
    public static void AddOrUpdate(
        IFileSystem fileSystem,
        string csprojPath,
        string packageId,
        string version,
        PackageAssets? declared = null,
        PackageAssets? previouslyWritten = null)
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
                AddBeforeTrailingImports(project, itemGroup);
            }

            existing = new XElement("PackageReference", new XAttribute("Include", packageId), new XAttribute("Version", version));
            itemGroup.Add(existing);
        }

        SyncAssets(existing, declared ?? PackageAssets.None, previouslyWritten ?? PackageAssets.None);

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

    /// <summary>Whether the <c>.csproj</c> references <paramref name="packageId"/> as a package at all.</summary>
    public static bool HasReference(IFileSystem fileSystem, string csprojPath, string packageId)
    {
        var (_, project) = Load(fileSystem, csprojPath);
        return FindReference(project, packageId) is not null;
    }

    /// <summary>
    /// The asset metadata currently on a package's reference, or null when the package has no
    /// reference at all. Used by <c>doctor</c> to compare the file against the manifest.
    /// </summary>
    public static PackageAssets? ReadAssets(IFileSystem fileSystem, string csprojPath, string packageId)
    {
        var (_, project) = Load(fileSystem, csprojPath);
        var existing = FindReference(project, packageId);

        return existing is null ? null : ReadAssets(existing);
    }

    private static PackageAssets ReadAssets(XElement reference) => new()
    {
        Include = ReadSlot(reference, "IncludeAssets"),
        Exclude = ReadSlot(reference, "ExcludeAssets"),
        Private = ReadSlot(reference, "PrivateAssets"),
    };

    /// <summary>
    /// Reads one metadata slot in either of MSBuild's two equivalent spellings.
    /// </summary>
    /// <remarks>
    /// <c>PrivateAssets="none"</c> and <c>&lt;PrivateAssets&gt;none&lt;/PrivateAssets&gt;</c> mean the
    /// same thing to MSBuild. dotquay writes the child-element form, but a hand-edited file may well
    /// use the attribute — and matching only one form would leave the writer adding a child element
    /// beside an existing attribute, so the same reference would carry the metadata twice with
    /// MSBuild's precedence, not dotquay, deciding the winner.
    /// </remarks>
    private static IReadOnlyList<string>? ReadSlot(XElement reference, string name)
    {
        var attribute = reference.Attribute(name);
        if (attribute is not null)
        {
            return PackageAssets.Parse(attribute.Value);
        }

        var child = reference.Element(name);
        return child is null ? null : PackageAssets.Parse(child.Value);
    }

    private static void SyncAssets(XElement reference, PackageAssets declared, PackageAssets previouslyWritten)
    {
        foreach (var (element, get) in AssetSlots)
        {
            var wanted = get(declared);

            if (wanted is not null)
            {
                WriteSlot(reference, element, wanted);
                continue;
            }

            // Not declared. Only ours to remove if we are the ones who put it there.
            if (get(previouslyWritten) is not null)
            {
                ClearSlot(reference, element);
            }
        }
    }

    /// <summary>Writes a slot, preserving whichever spelling the file already uses so dotquay does not
    /// rewrite a hand-authored file's style out from under its author.</summary>
    private static void WriteSlot(XElement reference, string name, IReadOnlyList<string> tags)
    {
        var value = PackageAssets.Format(tags);

        var attribute = reference.Attribute(name);
        if (attribute is not null)
        {
            attribute.Value = value;

            // A file carrying both spellings is already ambiguous; collapse it rather than leave two
            // sources of truth behind.
            reference.Element(name)?.Remove();
            return;
        }

        var child = reference.Element(name);
        if (child is not null)
        {
            child.Value = value;
            return;
        }

        reference.Add(new XElement(name, value));
    }

    private static void ClearSlot(XElement reference, string name)
    {
        reference.Attribute(name)?.Remove();
        reference.Element(name)?.Remove();
    }

    /// <summary>
    /// Adds a new <c>ItemGroup</c> at the end of <c>&lt;Project&gt;</c>, but above the dotquay overlay
    /// hook and any other trailing <c>&lt;Import&gt;</c>s (with the comments sitting on them).
    /// </summary>
    /// <remarks>
    /// Not cosmetic. <c>link</c> may already have appended the overlay hook as the last element, and
    /// MSBuild evaluates in order: a <c>PackageReference</c> declared below it would survive the
    /// overlay's <c>Remove</c>, leaving the build silently on the published package
    /// (docs/DESIGN-v0.11-LOCAL-DOTNET-LINK.md, "How the overlay works"). The hook is looked for
    /// explicitly as well, so a hand-edit that puts something after it still can't push the group below.
    /// </remarks>
    private static void AddBeforeTrailingImports(XElement project, XElement itemGroup)
    {
        XNode? anchor = project.Elements().FirstOrDefault(LocalTargetsWriter.IsHook);

        if (anchor is null)
        {
            // Walk back over the trailing run of imports and comments; the earliest import is the anchor.
            for (var node = project.LastNode; node is XElement { Name.LocalName: "Import" } or XComment; node = node.PreviousNode)
            {
                if (node is XElement)
                {
                    anchor = node;
                }
            }
        }

        if (anchor is null)
        {
            project.Add(itemGroup);
            return;
        }

        // Keep a comment that annotates the import (the hook writes one) attached to it. Load parses
        // without preserving whitespace, so comments are direct siblings.
        while (anchor.PreviousNode is XComment comment)
        {
            anchor = comment;
        }

        anchor.AddBeforeSelf(itemGroup);
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
