using Dotquay.Core.Abstractions;
using Dotquay.Core.Git;

namespace Dotquay.Core.Doctor;

/// <summary>
/// Finds Git LFS pointer files sitting in an installed addon: the placeholders git leaves behind
/// when a checkout ran without git-lfs.
/// </summary>
/// <remarks>
/// <para>
/// Needed even though the fetch now repairs or refuses (<see cref="LfsResolver"/>): an addon
/// installed by an older dotquay, or copied from another machine, is already on disk in this state,
/// with a lockfile hash that was computed over the placeholders and so calls it <c>clean</c>.
/// Nothing else notices — the first symptom is the editor failing to load the addon's native
/// library, which reads as a Godot or platform problem.
/// </para>
/// <para>
/// Purely local: it looks at files already on disk, with no git and no network, so it costs the
/// same as every other doctor check. Only files at most <see cref="LfsPointer.MaxPointerBytes"/>
/// long are read, so scanning an addon full of large binaries stays cheap.
/// </para>
/// </remarks>
public static class LfsPointerScanner {
    /// <summary>Paths of pointer files under <paramref name="addonPath"/>, relative to it, with
    /// forward slashes.</summary>
    public static IReadOnlyList<string> Scan(IFileSystem fileSystem, string addonPath) {
        if (!fileSystem.DirectoryExists(addonPath)) {
            return [];
        }

        var found = new List<string>();
        foreach (var file in fileSystem.EnumerateFiles(addonPath, recursive: true)) {
            if (fileSystem.GetFileLength(file) > LfsPointer.MaxPointerBytes) {
                continue;
            }

            if (LfsPointer.Matches(fileSystem.ReadAllBytes(file))) {
                found.Add(Relative(addonPath, file));
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static string Relative(string root, string file) {
        var relative = Path.GetRelativePath(root, file);
        return relative.Replace('\\', '/');
    }
}
