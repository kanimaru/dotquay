namespace Dotquay.Core.SelfUpdate;

/// <summary>
/// The naming scheme for the files a self-update leaves beside the executable.
/// </summary>
/// <remarks>
/// <para>Its own string helpers rather than <see cref="System.IO.Path"/>, for two reasons. Path
/// splitting here must behave the same whether the string came from a Windows process path or an
/// in-memory test filesystem, and an extension is decided by one rule — <c>.exe</c> or nothing —
/// rather than by "text after the last dot", which would read <c>dotquay.new-0.17.0</c> as having
/// the extension <c>.0</c> and hand the rename dance a name it could not parse back.</para>
/// </remarks>
internal static class ExecutablePaths
{
    public const string StagedInfix = ".new-";

    public const string RetiredInfix = ".old-";

    private const string WindowsExtension = ".exe";

    public static string DirectoryOf(string path)
    {
        var index = LastSeparator(path);
        return index < 0 ? string.Empty : path[..index];
    }

    public static string FileNameOf(string path)
    {
        var index = LastSeparator(path);
        return index < 0 ? path : path[(index + 1)..];
    }

    /// <summary>Splits a file name into the part that names the tool and the part the OS needs to
    /// consider it executable. Windows will not start a file without <c>.exe</c>, which is why the
    /// staged download has to carry it too — it is run with <c>--version</c> before it is trusted.</summary>
    public static (string Stem, string Extension) Split(string fileName) =>
        fileName.EndsWith(WindowsExtension, StringComparison.OrdinalIgnoreCase)
            ? (fileName[..^WindowsExtension.Length], fileName[^WindowsExtension.Length..])
            : (fileName, string.Empty);

    public static string Staged(string executablePath, string version) => Sibling(executablePath, StagedInfix, version);

    public static string Retired(string executablePath, string version) => Sibling(executablePath, RetiredInfix, version);

    /// <summary>The version encoded in a sibling's name, or null when the name is not one of ours.
    /// Used to find the newest retired binary without trusting file timestamps, which a copy or a
    /// restore-from-backup rewrites.</summary>
    public static string? VersionOfRetired(string executablePath, string candidatePath) =>
        VersionOfSibling(executablePath, candidatePath, RetiredInfix);

    /// <summary>The version encoded in a staged download's name, or null. Used to sweep a staged
    /// file an earlier, failed update left behind — which happens on Windows more often than it
    /// looks like it should, because a file that was just run stays mapped for a moment after the
    /// process exits and cannot be deleted in that window.</summary>
    public static string? VersionOfStaged(string executablePath, string candidatePath) =>
        VersionOfSibling(executablePath, candidatePath, StagedInfix);

    private static string? VersionOfSibling(string executablePath, string candidatePath, string infix)
    {
        var (stem, extension) = Split(FileNameOf(executablePath));
        var candidate = FileNameOf(candidatePath);

        var prefix = stem + infix;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal)
            || !candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = candidate[prefix.Length..^extension.Length];
        return version.Length == 0 ? null : version;
    }

    private static string Sibling(string executablePath, string infix, string version)
    {
        var directory = DirectoryOf(executablePath);
        var (stem, extension) = Split(FileNameOf(executablePath));
        var name = $"{stem}{infix}{version}{extension}";

        return directory.Length == 0 ? name : $"{directory}{SeparatorOf(executablePath)}{name}";
    }

    private static int LastSeparator(string path)
    {
        for (var i = path.Length - 1; i >= 0; i--)
        {
            if (path[i] is '/' or '\\')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Reuses the separator the caller's own path used, so a sibling of a Windows path is
    /// spelled like a Windows path and one of a POSIX path like a POSIX path — including inside the
    /// in-memory filesystem, which is neither.</summary>
    private static char SeparatorOf(string path)
    {
        var index = LastSeparator(path);
        return index < 0 ? '/' : path[index];
    }
}
