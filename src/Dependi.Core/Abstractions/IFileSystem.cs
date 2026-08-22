namespace Dependi.Core.Abstractions;

/// <summary>
/// Every disk access in Dependi.Core goes through this seam so the sync engine, manifest/lockfile
/// readers, and <see cref="Dependi.Core.Init.InitService"/> are testable without touching a real
/// filesystem (see docs/SPEC.md §12, "Testable without network/disk").
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    string ReadAllText(string path);

    void WriteAllText(string path, string contents);

    /// <summary>Binary-safe read. Addon files are routinely binary (.png, .ttf, .import, compiled
    /// shaders) — the sync engine and content-hashing must never round-trip them through text
    /// encoding, which corrupts arbitrary bytes.</summary>
    byte[] ReadAllBytes(string path);

    void WriteAllBytes(string path, byte[] contents);

    /// <summary>Creates the directory, including any missing parents. No-op if it already exists.</summary>
    void CreateDirectory(string path);

    /// <summary>Recursively deletes a directory and everything under it. No-op if it doesn't exist.</summary>
    void DeleteDirectory(string path);

    void DeleteFile(string path);

    /// <summary>
    /// Atomically (as far as the OS allows) makes <paramref name="destination"/> become what
    /// <paramref name="source"/> currently is, replacing any existing directory at
    /// <paramref name="destination"/>. This is the swap step of the sync engine's staging model
    /// (SPEC.md §9 step 5) — the project folder is never left half-updated.
    /// </summary>
    void MoveDirectory(string source, string destination);

    /// <summary>Enumerates file paths under <paramref name="path"/>, recursively by default.</summary>
    IEnumerable<string> EnumerateFiles(string path, bool recursive = true);

    /// <summary>Enumerates subdirectory paths under <paramref name="path"/>, top-level only by
    /// default — the opposite default from <see cref="EnumerateFiles"/>, since its one caller
    /// (<c>dependi doctor</c>'s "orphaned folders in addons/" check, SPEC.md §6 requirement P1-4)
    /// only ever wants the immediate children of <c>addons/</c>. Callers check
    /// <see cref="DirectoryExists"/> first, same convention already used before
    /// <see cref="EnumerateFiles"/>.</summary>
    IEnumerable<string> EnumerateDirectories(string path, bool recursive = false);

    string CombinePath(params string[] segments);

    string GetFullPath(string path);
}
