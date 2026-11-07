using Dotquay.Core.Abstractions;
using Dotquay.Core.Diagnostics;

namespace Dotquay.Core.IO;

/// <summary>Real, disk-backed <see cref="IFileSystem"/>. Everything else in Dotquay.Core takes
/// <see cref="IFileSystem"/> as a dependency instead of calling <see cref="System.IO"/> directly,
/// so this is the only type in the codebase allowed to do so.</summary>
public sealed class SystemFileSystem : IFileSystem {
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string contents) {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }

        DiagnosticLog.Write(DiagnosticLevel.Trace, "fs", $"write {path} ({contents.Length} chars)");
        File.WriteAllText(path, contents);
    }

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public void WriteAllBytes(string path, byte[] contents) {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }

        DiagnosticLog.Write(DiagnosticLevel.Trace, "fs", $"write {path} ({contents.Length} bytes)");
        File.WriteAllBytes(path, contents);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteDirectory(string path) {
        if (!Directory.Exists(path)) {
            return;
        }

        // A link is removed as a link, never recursed into. `addons/<folder>` can be a live link
        // into a developer's own checkout (SPEC.md §7.4), and a recursive delete through it would
        // destroy their working copy. Passing recursive: false on a reparse point removes the link
        // itself and leaves the target untouched.
        if (IsDirectoryLink(path)) {
            DiagnosticLog.Write(DiagnosticLevel.Verbose, "fs",
                $"delete link {path} (target left untouched)");
            Directory.Delete(path, recursive: false);
            return;
        }

        DiagnosticLog.Write(DiagnosticLevel.Verbose, "fs", $"delete tree {path}");
        DeleteTree(new DirectoryInfo(path));
    }

    /// <summary>
    /// Recursive delete that handles the two things <c>Directory.Delete(path, recursive: true)</c>
    /// cannot, both of which bit real users:
    /// <list type="bullet">
    /// <item><b>Read-only files.</b> The framework refuses them (UnauthorizedAccessException on
    /// Windows), and git marks every pack file it writes read-only. A cache entry left behind by a
    /// failed fetch could therefore never be cleared on retry: RepoCache threw on
    /// <c>.git/objects/pack/*.idx</c> on every later <c>update</c> of that addon until the user
    /// deleted <c>~/.dotquay/cache</c> by hand. Every tree dotquay deletes is content it owns
    /// (cache entries, staging, the addon folder it installed), so stripping the attribute is
    /// always the right call.</item>
    /// <item><b>Nested directory links.</b> The framework's recursion throws "access denied" on a
    /// junction inside the tree (measured on .NET 10 / Windows 11). A link is removed as an entry
    /// of its own and never entered, so the folder it points at is left untouched — the same
    /// invariant <see cref="DeleteDirectory"/> keeps for the root.</item>
    /// </list>
    /// </summary>
    private static void DeleteTree(DirectoryInfo directory) {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*",
            SearchOption.TopDirectoryOnly)) {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0) {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            switch (entry) {
                case DirectoryInfo link when (link.Attributes & FileAttributes.ReparsePoint) != 0:
                    link.Delete(recursive: false); // the link only; its target is not ours
                    break;
                case DirectoryInfo child:
                    DeleteTree(child);
                    break;
                default:
                    entry.Delete();
                    break;
            }
        }

        directory.Delete(recursive: false);
    }

    public bool IsDirectoryLink(string path) =>
        Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public string? ResolveDirectoryLinkTarget(string path) {
        if (!IsDirectoryLink(path)) {
            return null;
        }

        try {
            // returnFinalTarget walks a chain of links through to the real folder: two junctions
            // hopping to the same checkout are the same claim and must compare equal.
            var final = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            if (final is not null) {
                return Path.GetFullPath(final.FullName);
            }
        } catch (IOException) {
            // A link whose target is gone cannot be walked to the end. The immediate target is
            // still the claim it makes, and reporting that is what lets the caller say so by name.
        }

        var immediate = Directory.ResolveLinkTarget(path, returnFinalTarget: false);
        return immediate is null ? null : Path.GetFullPath(immediate.FullName);
    }

    public void CreateDirectoryLink(string linkPath, string targetPath) {
        DeleteDirectory(linkPath);

        var parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(parent)) {
            Directory.CreateDirectory(parent);
        }

        var target = Path.GetFullPath(targetPath);
        DiagnosticLog.Write(DiagnosticLevel.Verbose, "fs", $"link {linkPath} -> {target}");

        if (OperatingSystem.IsWindows()) {
            // A junction, not a symlink. Windows only lets an unprivileged process create a symlink
            // when Developer Mode is enabled, which is off by default and is not something a
            // package manager should ask a Godot user to turn on. Directory junctions have no such
            // requirement, and Godot follows them identically. Confirmed non-elevated on this
            // machine.
            CreateWindowsJunction(linkPath, target);
            return;
        }

        Directory.CreateSymbolicLink(linkPath, target);
    }

    /// <summary>Shells out to <c>cmd /c mklink /J</c>, in the same spirit as this project already
    /// shelling out to <c>git</c> and <c>dotnet</c> (SPEC.md §12): the OS tool does the real work
    /// and there is no supported .NET API for creating a junction.</summary>
    private static void CreateWindowsJunction(string linkPath, string targetPath) {
        var arguments = new[] { "/c", "mklink", "/J", linkPath, targetPath };
        var trace = ProcessTrace.Start("cmd.exe", arguments, workingDirectory: null);

        var startInfo = new System.Diagnostics.ProcessStartInfo {
            FileName = "cmd.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new IOException($"Could not start cmd.exe to link '{linkPath}'.");

        // Drained before waiting, and concurrently: mklink's output is tiny, but reading a
        // redirected stream only once the child has exited is the textbook full-pipe deadlock, and
        // tracing now wants both streams anyway.
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = standardErrorTask.GetAwaiter().GetResult();
        process.WaitForExit();
        trace.Completed(process.ExitCode, standardOutput, standardError);

        if (process.ExitCode != 0) {
            var error = standardError.Trim();
            throw new IOException(
                $"Could not create a directory junction at '{linkPath}' -> '{targetPath}'."
                + (error.Length > 0 ? $" {error}" : string.Empty));
        }
    }

    public void DeleteFile(string path) {
        if (File.Exists(path)) {
            DiagnosticLog.Write(DiagnosticLevel.Trace, "fs", $"delete {path}");
            File.Delete(path);
        }
    }

    public void MoveFile(string source, string destination, bool overwrite = false) {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }

        DiagnosticLog.Write(DiagnosticLevel.Trace, "fs", $"move {source} -> {destination}");
        File.Move(source, destination, overwrite);
    }

    public void SetExecutablePermissions(string path) {
        if (!File.Exists(path) || OperatingSystem.IsWindows()) {
            // Windows decides executability by extension, so there is nothing to set. Returning
            // rather than throwing keeps the self-update swap one code path on both platforms.
            return;
        }

        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(
            path,
            mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute);
    }

    public void SetOwnerOnlyPermissions(string path) {
        if (!File.Exists(path)) {
            return;
        }

        if (OperatingSystem.IsWindows()) {
            // No-op by design. The equivalent would be rewriting the file's DACL, which needs
            // Windows-only ACL APIs; the file already sits under %USERPROFILE%, whose default ACL
            // is already owner-only. `dotquay doctor` verifies that rather than assuming it.
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void MoveDirectory(string source, string destination) {
        // Same link guard as DeleteDirectory, for the same reason: the swap's destination is
        // addons/<folder>, which may currently be a link into someone's own checkout.
        DeleteDirectory(destination);

        var destinationParent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destinationParent)) {
            Directory.CreateDirectory(destinationParent);
        }

        // Directory.Move is atomic when source and destination are on the same volume, which
        // holds for the cache-to-project moves the sync engine performs (SPEC.md §9 step 5).
        DiagnosticLog.Write(DiagnosticLevel.Verbose, "fs", $"move tree {source} -> {destination}");
        Directory.Move(source, destination);
    }

    public IEnumerable<string> EnumerateFiles(string path, bool recursive = true) =>
        Directory.EnumerateFiles(path, "*", recursive ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly);

    public IEnumerable<string> EnumerateDirectories(string path, bool recursive = false) =>
        Directory.EnumerateDirectories(path, "*", recursive ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly);

    public string CombinePath(params string[] segments) => Path.Combine(segments);

    public string GetFullPath(string path) => Path.GetFullPath(path);
}
