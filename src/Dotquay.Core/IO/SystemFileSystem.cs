using Dotquay.Core.Abstractions;

namespace Dotquay.Core.IO;

/// <summary>Real, disk-backed <see cref="IFileSystem"/>. Everything else in Dotquay.Core takes
/// <see cref="IFileSystem"/> as a dependency instead of calling <see cref="System.IO"/> directly,
/// so this is the only type in the codebase allowed to do so.</summary>
public sealed class SystemFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, contents);
    }

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, contents);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // A link is removed as a link, never recursed into. `addons/<folder>` can be a live link into
        // a developer's own checkout (SPEC.md §7.4), and a recursive delete through it would destroy
        // their working copy. Passing recursive: false on a reparse point removes the link itself and
        // leaves the target untouched.
        if (IsDirectoryLink(path))
        {
            Directory.Delete(path, recursive: false);
            return;
        }

        Directory.Delete(path, recursive: true);
    }

    public bool IsDirectoryLink(string path) =>
        Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public void CreateDirectoryLink(string linkPath, string targetPath)
    {
        DeleteDirectory(linkPath);

        var parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var target = Path.GetFullPath(targetPath);

        if (OperatingSystem.IsWindows())
        {
            // A junction, not a symlink. Windows only lets an unprivileged process create a symlink
            // when Developer Mode is enabled, which is off by default and is not something a package
            // manager should ask a Godot user to turn on. Directory junctions have no such
            // requirement, and Godot follows them identically. Confirmed non-elevated on this machine.
            CreateWindowsJunction(linkPath, target);
            return;
        }

        Directory.CreateSymbolicLink(linkPath, target);
    }

    /// <summary>Shells out to <c>cmd /c mklink /J</c>, in the same spirit as this project already
    /// shelling out to <c>git</c> and <c>dotnet</c> (SPEC.md §12): the OS tool does the real work and
    /// there is no supported .NET API for creating a junction.</summary>
    private static void CreateWindowsJunction(string linkPath, string targetPath)
    {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", linkPath, targetPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new IOException($"Could not start cmd.exe to link '{linkPath}'.");

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd().Trim();
            throw new IOException(
                $"Could not create a directory junction at '{linkPath}' -> '{targetPath}'."
                + (error.Length > 0 ? $" {error}" : string.Empty));
        }
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void MoveDirectory(string source, string destination)
    {
        // Same link guard as DeleteDirectory, for the same reason: the swap's destination is
        // addons/<folder>, which may currently be a link into someone's own checkout.
        DeleteDirectory(destination);

        var destinationParent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destinationParent))
        {
            Directory.CreateDirectory(destinationParent);
        }

        // Directory.Move is atomic when source and destination are on the same volume, which
        // holds for the cache-to-project moves the sync engine performs (SPEC.md §9 step 5).
        Directory.Move(source, destination);
    }

    public IEnumerable<string> EnumerateFiles(string path, bool recursive = true) =>
        Directory.EnumerateFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

    public IEnumerable<string> EnumerateDirectories(string path, bool recursive = false) =>
        Directory.EnumerateDirectories(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

    public string CombinePath(params string[] segments) => Path.Combine(segments);

    public string GetFullPath(string path) => Path.GetFullPath(path);
}
