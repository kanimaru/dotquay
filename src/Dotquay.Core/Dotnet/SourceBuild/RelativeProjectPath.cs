namespace Dotquay.Core.Dotnet.SourceBuild;

/// <summary>
/// Resolves a <c>ProjectReference</c>'s <c>Include</c> against the project file that holds it, purely
/// lexically: separators unified to <c>/</c>, <c>.</c> dropped, <c>..</c> folded.
/// </summary>
/// <remarks>
/// <para>
/// Lexical on purpose. <see cref="Abstractions.IFileSystem"/> has no "directory of" or "normalize"
/// operation, its in-memory double does not fold <c>..</c> at all, and a <c>Path.GetFullPath</c> would
/// silently resolve against the process's working directory rather than the project's. Doing it here, in
/// one tested place, gives the real file system and the test double the same string.
/// </para>
/// <para>
/// Both separators are accepted because Godot projects are written on Windows (<c>..\src\X.csproj</c>,
/// what Godot and Visual Studio emit) and built on Linux CI, and MSBuild accepts either on both.
/// </para>
/// </remarks>
internal static class RelativeProjectPath {
    public static string Resolve(string fromProjectFile, string include) {
        var target = include.Trim().Replace('\\', '/');

        string root;
        List<string> segments;

        if (IsRooted(target)) {
            (root, segments) = Split(target);
        } else {
            var from = fromProjectFile.Replace('\\', '/');
            var lastSlash = from.LastIndexOf('/');
            (root, segments) = Split(lastSlash < 0 ? string.Empty : from[..lastSlash]);
            segments.AddRange(target.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        var folded = new List<string>(segments.Count);
        foreach (var segment in segments) {
            switch (segment) {
                case ".":
                    continue;
                case ".." when folded.Count > 0 && folded[^1] != "..":
                    folded.RemoveAt(folded.Count - 1);
                    continue;
                case ".." when root.Length > 0:
                    // Above a root there is nothing; the OS clamps the same way.
                    continue;
                default:
                    folded.Add(segment);
                    break;
            }
        }

        return root + string.Join('/', folded);
    }

    private static bool IsRooted(string path) => path.StartsWith('/') || HasDriveLetter(path);

    private static bool HasDriveLetter(string path) => path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]);

    private static (string Root, List<string> Segments) Split(string path) {
        var root = string.Empty;

        if (path.StartsWith("//", StringComparison.Ordinal)) {
            root = "//";
        } else if (path.StartsWith('/')) {
            root = "/";
        } else if (HasDriveLetter(path)) {
            root = path[..2] + "/";
            path = path[2..];
        }

        return (root, path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList());
    }
}
