using System.Xml;
using System.Xml.Linq;
using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Dotnet.SourceBuild;

/// <summary>One <c>ProjectReference</c> in the consuming project, resolved as far as it can be.</summary>
/// <param name="Include">The reference exactly as the <c>.csproj</c> spells it - what every message
/// quotes, because it is what the user will search their file for.</param>
/// <param name="ProjectPath">Where it points, resolved against the <c>.csproj</c>'s folder.</param>
/// <param name="Condition">Null when the reference is unconditional; otherwise the condition that
/// guards it, or a description of the <c>&lt;Choose&gt;</c> branch it sits in.</param>
/// <param name="Identity">What the referenced project builds.</param>
public sealed record SourceReference(string Include, string ProjectPath, string? Condition, ProjectIdentity Identity) {
    public bool IsUnconditional => Condition is null;

    public bool Builds(string packageId) =>
        Identity.PackageId is { } id && string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Answers "does this project already build that package from source?" from its <c>.csproj</c>'s
/// <c>ProjectReference</c>s (docs/DESIGN-v0.26-SOURCE-BUILT-PACKAGES.md §A).
/// </summary>
/// <remarks>
/// <para>
/// Read-only, always. dotquay never writes, moves or deletes a <c>ProjectReference</c>; this type exists so
/// the one writer that does touch the file (<see cref="CsprojPackageReferenceWriter"/>, through
/// <see cref="DotnetInstaller"/>) stops contradicting one.
/// </para>
/// <para>
/// Every doubt resolves to "no provider": a reference whose include is a wildcard or a property, whose
/// target is missing or unreadable, or whose identity needs MSBuild to evaluate, provides nothing. That is
/// what keeps the feature from ever removing a <c>PackageReference</c> a build still needs - the worst it
/// can do is what dotquay already did before it existed.
/// </para>
/// </remarks>
public static class SourceProvidedPackages {
    /// <summary>The unconditional reference that builds <paramref name="packageId"/>, or null.</summary>
    public static SourceReference? FindProvider(IFileSystem fileSystem, string csprojPath, string packageId) =>
        ReferencesTo(fileSystem, csprojPath, packageId).FirstOrDefault(r => r.IsUnconditional);

    /// <summary>Every reference that builds <paramref name="packageId"/>, conditional ones included -
    /// what <c>doctor</c> and <c>link</c> need, where a conditional source build still matters.</summary>
    public static IReadOnlyList<SourceReference> ReferencesTo(IFileSystem fileSystem, string csprojPath, string packageId) =>
        ReadAll(fileSystem, csprojPath).Where(r => r.Builds(packageId)).ToList();

    /// <summary>Every <c>ProjectReference</c> in <paramref name="csprojPath"/> that survives its own file's
    /// <c>Remove</c> items. An unreadable file has none: the writer reports that problem in its own words
    /// the moment it touches the file.</summary>
    public static IReadOnlyList<SourceReference> ReadAll(IFileSystem fileSystem, string csprojPath) {
        if (!fileSystem.FileExists(csprojPath)) {
            return [];
        }

        XElement? project;
        try {
            project = XDocument.Parse(fileSystem.ReadAllText(csprojPath)).Root;
        } catch (XmlException) {
            return [];
        }

        if (project is null) {
            return [];
        }

        var elements = project.Descendants().Where(e => e.Name.LocalName == "ProjectReference").ToList();

        // A <ProjectReference Remove="..."/> cancels an include. Honouring it can only turn a provider into a
        // non-provider, which falls back to writing the PackageReference - the safe direction.
        var removed = elements
            .SelectMany(e => Items((string?)e.Attribute("Remove")))
            .Select(item => RelativeProjectPath.Resolve(csprojPath, item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var references = new List<SourceReference>();
        foreach (var element in elements) {
            foreach (var include in Items((string?)element.Attribute("Include"))) {
                if (IsUnevaluable(include)) {
                    continue;
                }

                var target = RelativeProjectPath.Resolve(csprojPath, include);
                if (removed.Contains(target)) {
                    continue;
                }

                references.Add(new SourceReference(
                    include, target, ConditionOf(element), ProjectIdentity.Read(fileSystem, target)));
            }
        }

        return references;
    }

    /// <summary>MSBuild item lists are <c>;</c>-separated.</summary>
    private static IEnumerable<string> Items(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsUnevaluable(string include) =>
        include.Contains('*', StringComparison.Ordinal)
        || include.Contains('?', StringComparison.Ordinal)
        || include.Contains("$(", StringComparison.Ordinal)
        || include.Contains("@(", StringComparison.Ordinal)
        || include.Contains("%(", StringComparison.Ordinal);

    /// <summary>The first condition between the reference and the project root, if any.</summary>
    private static string? ConditionOf(XElement reference) {
        for (var node = reference; node is not null && node.Name.LocalName != "Project"; node = node.Parent) {
            if (MsbuildProperties.HasCondition(node)) {
                return ((string)node.Attribute("Condition")!).Trim();
            }

            if (node.Name.LocalName == "Otherwise") {
                return "the <Otherwise> branch of a <Choose>";
            }
        }

        return null;
    }
}
