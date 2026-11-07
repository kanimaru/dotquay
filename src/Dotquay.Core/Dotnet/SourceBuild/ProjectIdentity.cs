using System.Xml;
using System.Xml.Linq;
using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Dotnet.SourceBuild;

/// <summary>
/// What a referenced project builds, as far as its own file states it literally.
/// </summary>
/// <param name="PackageId">The package id the project produces, or null when it cannot be told without
/// evaluating MSBuild (a <c>$(...)</c> value, a conditional definition, an unreadable file). Null never
/// matches anything - see <see cref="SourceProvidedPackages"/>.</param>
/// <param name="Version">The literal version the project builds, or null when it states none (MSBuild's
/// default then applies, which is not this project's statement) or cannot be told.</param>
public sealed record ProjectIdentity(string? PackageId, string? Version) {
    public static ProjectIdentity Unknown { get; } = new(null, null);

    /// <summary>
    /// Reads the identity from the project file at <paramref name="projectPath"/>, following MSBuild's
    /// own defaults: <c>PackageId</c>, else <c>AssemblyName</c>, else the file name;
    /// <c>Version</c>, else <c>VersionPrefix</c>[-<c>VersionSuffix</c>].
    /// </summary>
    /// <remarks>
    /// Only the file itself is read. A <c>Directory.Build.props</c> or another import that sets these
    /// properties is invisible here (DESIGN-v0.26 §A.4); the fallback to the file name is what MSBuild
    /// does when nothing sets them, which is the overwhelmingly common case for a library in the same
    /// repository.
    /// </remarks>
    public static ProjectIdentity Read(IFileSystem fileSystem, string projectPath) {
        if (!fileSystem.FileExists(projectPath)) {
            return Unknown;
        }

        XElement? project;
        try {
            project = XDocument.Parse(fileSystem.ReadAllText(projectPath)).Root;
        } catch (XmlException) {
            return Unknown;
        }

        if (project is null || project.Name.LocalName != "Project") {
            return Unknown;
        }

        var properties = new MsbuildProperties(project);

        var packageId = properties.Get("PackageId") switch {
            { IsKnown: true } known => known.Value,
            { IsAbsent: true } => properties.Get("AssemblyName") switch {
                { IsKnown: true } known => known.Value,
                { IsAbsent: true } => FileStem(projectPath),
                _ => null,
            },
            _ => null,
        };

        return new ProjectIdentity(packageId, ReadVersion(properties));
    }

    private static string? ReadVersion(MsbuildProperties properties) {
        var version = properties.Get("Version");
        if (!version.IsAbsent) {
            return version.IsKnown ? version.Value : null;
        }

        var prefix = properties.Get("VersionPrefix");
        if (!prefix.IsKnown) {
            return null;
        }

        var suffix = properties.Get("VersionSuffix");
        return suffix switch {
            { IsAbsent: true } => prefix.Value,
            { IsKnown: true } => $"{prefix.Value}-{suffix.Value}",
            _ => null,
        };
    }

    private static string FileStem(string path) {
        var name = path.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }
}

/// <summary>
/// A project file's top-level properties, each in one of three states: absent, known (one literal,
/// unconditional value), or unknown (anything MSBuild would have to evaluate).
/// </summary>
/// <remarks>
/// Three states, not a nullable string, because the two "no value" cases mean opposite things. Absent
/// lets MSBuild's default apply, so the reader may fall through to the next source. Unknown means some
/// value is set and dotquay cannot tell which, so falling through would guess - and a wrong guess here
/// is a <c>PackageReference</c> silently left out of somebody's build.
/// </remarks>
internal sealed class MsbuildProperties(XElement project) {
    public readonly record struct Property(bool IsAbsent, bool IsKnown, string? Value) {
        public static Property Absent { get; } = new(true, false, null);

        public static Property Unknown { get; } = new(false, false, null);

        public static Property Known(string value) => new(false, true, value);
    }

    public Property Get(string name) {
        var definitions = project.Elements()
            .Where(e => e.Name.LocalName == "PropertyGroup")
            .SelectMany(group => group.Elements().Where(e => e.Name.LocalName == name).Select(e => (Group: group, Element: e)))
            .ToList();

        // A property defined inside <Choose> lives under When/Otherwise, not directly under Project, and so
        // is not in the list above; it still decides the value, so its existence alone makes it unknown.
        if (project.Descendants().Any(e => e.Name.LocalName == name && e.Ancestors().Any(IsChooseBranch))) {
            return Property.Unknown;
        }

        if (definitions.Count == 0) {
            return Property.Absent;
        }

        if (definitions.Any(d => HasCondition(d.Group) || HasCondition(d.Element))) {
            return Property.Unknown;
        }

        // Later definitions override earlier ones in MSBuild's evaluation order.
        var value = definitions[^1].Element.Value.Trim();

        if (value.Length == 0) {
            // An empty PackageId/AssemblyName/Version is what the SDK's defaults test for, so it behaves as
            // if never set.
            return Property.Absent;
        }

        return value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal)
            ? Property.Unknown
            : Property.Known(value);
    }

    internal static bool HasCondition(XElement element) =>
        !string.IsNullOrWhiteSpace((string?)element.Attribute("Condition"));

    internal static bool IsChooseBranch(XElement element) =>
        element.Name.LocalName is "Choose" or "When" or "Otherwise";
}
