using System.Security.Cryptography;
using System.Text;
using Dependi.Core.Abstractions;

namespace Dependi.Core.Sync;

/// <summary>
/// Computes a deterministic content hash of a directory tree — used to record <c>content_hash</c>
/// in the lockfile after install (SPEC.md §7.3) and to detect hand-modifications before
/// overwriting an addon (SPEC.md §9 step 4, requirement P0-6). Deterministic regardless of
/// filesystem enumeration order: relative paths are sorted before hashing, and each path is mixed
/// in alongside its content hash so a rename can never hash the same as the original layout.
/// </summary>
public static class ContentHasher
{
    /// <summary>Returns a <c>"sha256:&lt;hex&gt;"</c> string, matching the lockfile's
    /// <c>content_hash</c> format (SPEC.md §7.3).</summary>
    public static string HashDirectory(IFileSystem fileSystem, string path)
    {
        var relativePaths = fileSystem.EnumerateFiles(path, recursive: true)
            .Select(file => NormalizeRelative(file, path))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var relative in relativePaths)
        {
            var fullPath = fileSystem.CombinePath(path, relative);
            var fileContentHash = SHA256.HashData(fileSystem.ReadAllBytes(fullPath));

            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]); // separator: prevents "ab" + "c" colliding with "a" + "bc"
            hash.AppendData(fileContentHash);
        }

        var digest = hash.GetHashAndReset();
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string NormalizeRelative(string filePath, string root)
    {
        var relative = filePath[root.Length..].TrimStart('/', '\\');
        return relative.Replace('\\', '/');
    }
}
