using System.Xml.Linq;
using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Dotnet;

/// <summary>
/// Decides whether a project looks like something other projects consume as a NuGet package
/// (docs/DESIGN-v0.14-FEED-COMMANDS.md, D10).
/// </summary>
/// <remarks>
/// <para>This exists to decide whether a question is worth asking, and nothing else. A Godot game is
/// the end of the line: nothing depends on it as a package, so whether a source generator flows onward
/// has no consequence there and no right answer to get wrong. Asking anyway, on every generator added
/// to every game, is how a useful prompt becomes one people learn to dismiss.</para>
/// <para>Deliberately a heuristic, and deliberately one that only gates a prompt. Every SDK project is
/// technically packable — <c>IsPackable</c> defaults to true for a library — so "could this be packed"
/// is not the question; "did anyone set this project up to be packed" is. Guessing wrong in the quiet
/// direction costs one skipped question and leaves behaviour exactly as it was before v0.14; guessing
/// wrong in the loud direction costs one question the user can answer with Enter. Neither guess ever
/// decides what gets written — that is the user's answer, or a flag.</para>
/// </remarks>
public static class CsprojPackable {
    /// <summary>
    /// The <c>.csproj</c> property suggesting this project is published, or null when none is.
    /// </summary>
    /// <remarks>
    /// Returns the property <em>name</em> rather than a bool so the prompt can say what it read. The
    /// probe is a guess, and a user being asked an unexpected question about their own project is owed
    /// the evidence behind it.
    /// </remarks>
    public static string? Evidence(IFileSystem fileSystem, string csprojPath) {
        if (!fileSystem.FileExists(csprojPath)) {
            return null;
        }

        XDocument document;
        try {
            document = XDocument.Parse(fileSystem.ReadAllText(csprojPath));
        } catch (System.Xml.XmlException) {
            // A malformed .csproj is a real problem and emphatically not this method's to report: the
            // build will say so far more usefully. Staying quiet here means a broken project file never
            // turns into a confusing question about source generators.
            return null;
        }

        // Collected before anything is decided, rather than returning from inside the walk: the
        // properties are read in document order and precedence must not depend on which the author
        // happened to type first. A project with both PackageId and IsPackable=false has made a clear
        // statement, and it should read the same whichever line comes second.
        //
        // LocalName throughout: an SDK-style .csproj normally has no namespace, but the older MSBuild
        // namespace is still legal and still builds, and a namespace-sensitive match would silently
        // read every property as absent.
        var properties = document.Descendants()
            .Where(element => element.Name.LocalName == "PropertyGroup")
            .SelectMany(group => group.Elements())
            .GroupBy(property => property.Name.LocalName, StringComparer.OrdinalIgnoreCase)

            // Last one wins, matching MSBuild: a later PropertyGroup overrides an earlier one.
            .ToDictionary(group => group.Key, group => group.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);

        // An explicit false settles it outright, whatever else the file says — it is the one
        // unambiguous statement a project can make about this.
        if (properties.TryGetValue("IsPackable", out var isPackable) && IsFalse(isPackable)) {
            return null;
        }

        // An explicit PackageId is the strongest positive signal: nobody sets it on a project they do
        // not intend to publish.
        if (properties.TryGetValue("PackageId", out var packageId) && packageId.Length > 0) {
            return "PackageId";
        }

        if (properties.TryGetValue("GeneratePackageOnBuild", out var onBuild) && IsTrue(onBuild)) {
            return "GeneratePackageOnBuild";
        }

        // Only an *explicit* true counts. The default is already true for a library, so treating an
        // absent value as packable would make every project match and the prompt universal.
        return isPackable is not null && IsTrue(isPackable) ? "IsPackable" : null;
    }

    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalse(string value) => string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}
