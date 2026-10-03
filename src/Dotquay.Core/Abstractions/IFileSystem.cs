namespace Dotquay.Core.Abstractions;

/// <summary>
/// Every disk access in Dotquay.Core goes through this seam so the sync engine, manifest/lockfile
/// readers, and <see cref="Dotquay.Core.Init.InitService"/> are testable without touching a real
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

    /// <summary>Size of a file in bytes, without reading it. For checks that must look at many files
    /// and only care about the small ones.</summary>
    long GetFileLength(string path);

    void WriteAllBytes(string path, byte[] contents);

    /// <summary>Creates the directory, including any missing parents. No-op if it already exists.</summary>
    void CreateDirectory(string path);

    /// <summary>
    /// Recursively deletes a directory and everything under it. No-op if it doesn't exist.
    /// </summary>
    /// <remarks>
    /// Implementations MUST NOT follow a directory link (SPEC.md §7.4): when
    /// <paramref name="path"/> is a link, only the link itself is removed and the folder it points at
    /// is left completely alone. dotquay deletes <c>addons/&lt;folder&gt;</c> in several places —
    /// orphan pruning, <c>remove</c>, and the <c>install_as</c> rename — and with local links that
    /// folder can be a live link into a developer's own checkout of the addon. Recursing through one
    /// would delete their working copy, uncommitted work included.
    /// </remarks>
    void DeleteDirectory(string path);

    /// <summary>Whether <paramref name="path"/> is a directory link (a junction on Windows, a symlink
    /// elsewhere) rather than a real directory.</summary>
    bool IsDirectoryLink(string path);

    /// <summary>
    /// The absolute path <paramref name="path"/> points at when it is a directory link, or
    /// <c>null</c> when it is not one.
    /// </summary>
    /// <remarks>
    /// <see cref="IsDirectoryLink"/> answers <em>whether</em>; deciding whether it is safe to write
    /// answers <em>where</em> (docs/DESIGN-v0.16-LINK-SAFETY.md). A link agreeing with what the
    /// project declares may be re-asserted; one pointing somewhere else is a folder belonging to
    /// someone else, and package content must never travel through it into their repository.
    /// </remarks>
    string? ResolveDirectoryLinkTarget(string path);

    /// <summary>
    /// Creates a directory link at <paramref name="linkPath"/> pointing at
    /// <paramref name="targetPath"/>, replacing anything already there.
    /// </summary>
    /// <remarks>
    /// Backs <c>dotquay link</c> (SPEC.md §7.4). A link rather than a copy because the whole point is
    /// that edits in the developer's checkout are visible to the project immediately — a copy would
    /// need re-running on every keystroke, which is the workflow this replaces.
    /// </remarks>
    void CreateDirectoryLink(string linkPath, string targetPath);

    void DeleteFile(string path);

    /// <summary>
    /// Moves a single file, replacing <paramref name="destination"/> when
    /// <paramref name="overwrite"/> is set.
    /// </summary>
    /// <remarks>
    /// Added for the self-update swap (docs/DESIGN-v0.17-SELF-UPDATE.md), which is the one operation
    /// in dotquay that must move a file that is currently <em>running</em>. Windows refuses to delete
    /// a running image but allows renaming it, so "replace the executable" is spelled as two renames
    /// inside one directory rather than a delete and a write. A same-directory move is as close to
    /// atomic as the OS offers, which is what keeps the window where no dotquay exists down to one
    /// rename.
    /// </remarks>
    void MoveFile(string source, string destination, bool overwrite = false);

    /// <summary>
    /// Adds the executable bit for owner, group and other on Unix. No-op on Windows, where
    /// executability comes from the file extension, and on a file that doesn't exist.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="SetOwnerOnlyPermissions"/>, and here for the same reason: a
    /// downloaded release binary arrives as plain bytes carrying no mode at all, and a dotquay that
    /// installed itself without this would be a dotquay that cannot be run.
    /// </remarks>
    void SetExecutablePermissions(string path);

    /// <summary>
    /// Restricts <paramref name="path"/> so only its owner can read or write it (<c>0600</c> on
    /// Unix). No-op on Windows and when the file doesn't exist.
    /// </summary>
    /// <remarks>
    /// Exists for exactly one caller — <see cref="Dotquay.Core.Auth.TomlCredentialStore"/>, which
    /// writes a file full of secrets (SPEC.md §11.2). It lives on this interface rather than being a
    /// direct <c>File.SetUnixFileMode</c> call so the store stays behind the filesystem seam and the
    /// permission behaviour is assertable against a fake, like every other disk effect in this
    /// codebase.
    /// </remarks>
    void SetOwnerOnlyPermissions(string path);

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
    /// (<c>dotquay doctor</c>'s "orphaned folders in addons/" check, SPEC.md §6 requirement P1-4)
    /// only ever wants the immediate children of <c>addons/</c>. Callers check
    /// <see cref="DirectoryExists"/> first, same convention already used before
    /// <see cref="EnumerateFiles"/>.</summary>
    IEnumerable<string> EnumerateDirectories(string path, bool recursive = false);

    string CombinePath(params string[] segments);

    string GetFullPath(string path);
}
