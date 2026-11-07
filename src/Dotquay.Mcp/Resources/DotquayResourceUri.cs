namespace Dotquay.Mcp.Resources;

/// <summary>
/// Builds and parses the <c>dotquay:///</c> resource URIs (design doc §7): one project's manifest,
/// lockfile, local-link overlay, or one addon's captured patch.
/// </summary>
/// <remarks>
/// Built and parsed by hand, never through <see cref="Uri"/>: a Windows project path
/// (<c>F:\projects\godot\dotquay</c>) contains both a colon and backslashes, which <see cref="Uri"/>
/// parses as authority/scheme syntax rather than opaque data — exactly the ambiguity that sank the
/// idea of building these with <c>UriBuilder</c>. Percent-encoding the whole path into a single path
/// segment (<see cref="Uri.EscapeDataString"/>) sidesteps it entirely: everything between the fixed
/// <c>dotquay:///</c> prefix and the next literal <c>/</c> is the encoded project path and is never
/// inspected further, so a colon or backslash inside it can never be mistaken for URI syntax.
/// </remarks>
public static class DotquayResourceUri {
    private const string Prefix = "dotquay:///";

    public static string Manifest(string projectDirectory) => Build(projectDirectory, "manifest");

    public static string Lockfile(string projectDirectory) => Build(projectDirectory, "lockfile");

    public static string LocalLinks(string projectDirectory) => Build(projectDirectory, "local-links");

    public static string Patch(string projectDirectory, string addonName) =>
        Build(projectDirectory, "patches", Uri.EscapeDataString(addonName));

    private static string Build(string projectDirectory, params string[] segments) =>
        Prefix + Uri.EscapeDataString(Path.GetFullPath(projectDirectory)) + "/" + string.Join('/', segments);

    /// <summary>
    /// Splits a <c>dotquay:///</c> URI back into the decoded project path and the segments naming the
    /// resource within it.
    /// </summary>
    /// <remarks>Returns false for anything that isn't one of ours, rather than throwing — an
    /// unrecognized URI is <see cref="ResourceRegistry"/>'s problem to report as a missing resource,
    /// not this parser's to crash on.</remarks>
    public static bool TryParse(string uri, out string projectDirectory, out IReadOnlyList<string> segments) {
        projectDirectory = "";
        segments = [];

        if (!uri.StartsWith(Prefix, StringComparison.Ordinal)) {
            return false;
        }

        var rest = uri[Prefix.Length..];
        var slash = rest.IndexOf('/');

        if (slash < 0) {
            return false;
        }

        try {
            projectDirectory = Uri.UnescapeDataString(rest[..slash]);
            segments = rest[(slash + 1)..]
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToList();
            return segments.Count > 0;
        } catch (FormatException) {
            return false;
        }
    }
}
