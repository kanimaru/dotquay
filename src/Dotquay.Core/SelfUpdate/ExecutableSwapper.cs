using Dotquay.Core.Abstractions;

namespace Dotquay.Core.SelfUpdate;

/// <summary>
/// Replaces a running executable with a staged one, and puts it back.
/// </summary>
/// <remarks>
/// <para>One code path for every operating system. Windows cannot delete a running image but can
/// rename it, so the swap is two renames inside one directory; Linux would tolerate an unlink and a
/// write, but a second code path would mean the one nobody develops on is the one that breaks. Two
/// renames on the same volume is also the smallest window this can be done in without kernel
/// support — there is no instant at which no dotquay exists, only a brief one where it is called
/// something else.</para>
/// <para>The retired binary is deliberately kept rather than deleted. It is what
/// <c>dotquay self rollback</c> restores, and keeping exactly one of them (see
/// <see cref="Sweep"/>) is the difference between a safety net and a directory slowly filling with
/// every dotquay ever installed.</para>
/// </remarks>
public sealed class ExecutableSwapper {
    private readonly IFileSystem _fileSystem;

    public ExecutableSwapper(IFileSystem fileSystem) {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Makes <paramref name="stagedPath"/> become <paramref name="executablePath"/>, returning
    /// where the previous binary was moved to.
    /// </summary>
    /// <remarks>
    /// The staged file is expected to have been hashed and run already
    /// (<see cref="SelfUpdateWorkflow"/> does both) — by the time anything is renamed, the only
    /// remaining question is whether the filesystem cooperates. If the second rename fails, the
    /// first is undone, so a failure leaves the installation exactly as it was rather than
    /// headless.
    /// </remarks>
    public string Swap(string executablePath, string stagedPath, string retiringVersion) {
        var retiredPath = ExecutablePaths.Retired(executablePath, retiringVersion);

        if (string.Equals(retiredPath, stagedPath, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidOperationException(
                $"Refusing to swap '{stagedPath}' onto itself. This is a bug: the staged binary "
                + "and the path the current one would be retired to are the same file.");
        }

        _fileSystem.DeleteFile(retiredPath);
        _fileSystem.MoveFile(executablePath, retiredPath, overwrite: true);

        try {
            _fileSystem.MoveFile(stagedPath, executablePath, overwrite: true);
        } catch {
            // The window this exists for is one rename wide, and it is the only window in which
            // this machine has no dotquay. Putting the old one back is more important than
            // reporting cleanly, so the restore runs before the original failure is rethrown.
            if (!_fileSystem.FileExists(executablePath) && _fileSystem.FileExists(retiredPath)) {
                _fileSystem.MoveFile(retiredPath, executablePath, overwrite: true);
            }

            throw;
        }

        _fileSystem.SetExecutablePermissions(executablePath);
        return retiredPath;
    }

    /// <summary>Every retired binary beside <paramref name="executablePath"/>, newest first.
    /// Ordered by the version in the file name, never by timestamp: a copy, a restore from backup,
    /// or an archive extraction rewrites mtimes, and this list decides what a rollback
    /// restores.</summary>
    public IReadOnlyList<RetiredBinary> Retired(string executablePath) {
        var directory = ExecutablePaths.DirectoryOf(executablePath);
        if (directory.Length > 0 && !_fileSystem.DirectoryExists(directory)) {
            return [];
        }

        var found = new List<RetiredBinary>();

        foreach (var path in _fileSystem.EnumerateFiles(directory.Length == 0 ? "." : directory,
            recursive: false)) {
            if (ExecutablePaths.VersionOfRetired(executablePath, path) is { } version) {
                found.Add(new RetiredBinary(path, version));
            }
        }

        return found.OrderByDescending(entry => entry.Version,
            Comparer<string>.Create(VersionOrder.Compare)).ToList();
    }

    /// <summary>Keeps the newest retired binary and deletes the rest. Best-effort: a file the OS
    /// will not let go of (a virus scanner holding the image dotquay was running from a moment ago
    /// is the realistic case) is left for the next run rather than failing an update that already
    /// succeeded.</summary>
    public void Sweep(string executablePath) {
        foreach (var stale in Retired(executablePath).Skip(1)) {
            TryDelete(stale.Path);
        }

        SweepStaged(executablePath);
    }

    /// <summary>
    /// Removes staged downloads an earlier update left behind.
    /// </summary>
    /// <remarks>
    /// Not hypothetical tidying. A staged binary is <em>run</em> before it is trusted, and Windows
    /// keeps an executable's image mapped for a moment after the process exits — long enough that
    /// deleting it right there answers "access is denied". The update that rejected it is right to
    /// give up on the cleanup rather than turn it into the error the user reads, so the file is
    /// collected on the next attempt instead. Observed on a real published binary, not reasoned
    /// about.
    /// </remarks>
    public void SweepStaged(string executablePath) {
        var directory = ExecutablePaths.DirectoryOf(executablePath);
        if (directory.Length > 0 && !_fileSystem.DirectoryExists(directory)) {
            return;
        }

        foreach (var path in _fileSystem.EnumerateFiles(directory.Length == 0 ? "." : directory,
            recursive: false).ToList()) {
            if (ExecutablePaths.VersionOfStaged(executablePath, path) is not null) {
                TryDelete(path);
            }
        }
    }

    private void TryDelete(string path) {
        try {
            _fileSystem.DeleteFile(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }
}

/// <param name="Path">Where the retired binary sits.</param>
/// <param name="Version">The version it reports, as encoded in its file name.</param>
public sealed record RetiredBinary(string Path, string Version);
