using System.Diagnostics;
using Dotquay.Core.Git;
using Dotquay.Core.IO;
using Dotquay.Core.Sync;
using Xunit;

namespace Dotquay.Tests.Git;

/// <summary>
/// One commit, fetched by <see cref="ProcessGitClient"/> the way two differently configured machines would fetch
/// it: the bytes on disk differ (CRLF on one, LF on the other) and the recorded content hash must not.
/// </summary>
/// <remarks>
/// Needs a real git: it is git's own <c>core.autocrlf</c> / <c>core.eol</c> handling that makes the files differ,
/// and no fake reproduces that. The machines are made deterministic with <c>GIT_CONFIG_GLOBAL</c> pointing at a
/// file the test writes, not at whatever the developer running the suite has configured. Returns early, passing,
/// without git (same convention as the other real-process tests).
/// </remarks>
public sealed class ProcessGitClientLineEndingsRealProcessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotquay-eol-real-").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private static (int Exit, string Out) Git(string dir, IReadOnlyDictionary<string, string> env, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        foreach (var (k, v) in env) info.Environment[k] = v;
        using var p = Process.Start(info)!;
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, o.Result);
    }

    private static bool GitAvailable()
    {
        try { return Git(Path.GetTempPath(), new Dictionary<string, string>(), "--version").Exit == 0; }
        catch { return false; }
    }

    private Dictionary<string, string> Machine(string name, string config)
    {
        var path = Path.Combine(_root, name + ".gitconfig");
        File.WriteAllText(path, "[user]\n\tname = t\n\temail = t@example.com\n" + config);
        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = path, ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_TERMINAL_PROMPT"] = "0",
        };
    }

    /// <summary>A bare repository whose commit holds LF text and a binary containing CRLF bytes and a NUL.</summary>
    private (string Remote, string Sha) MakeRemote()
    {
        var author = Machine("author", "[core]\n\tautocrlf = false\n");
        var remote = Path.Combine(_root, "remote.git");
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(remote);
        Directory.CreateDirectory(work);
        Assert.Equal(0, Git(remote, author, "init", "--bare", "--quiet").Exit);
        Assert.Equal(0, Git(work, author, "init", "--quiet").Exit);
        Assert.Equal(0, Git(work, author, "remote", "add", "origin", remote).Exit);

        var addon = Path.Combine(work, "addons", "thing");
        Directory.CreateDirectory(addon);
        File.WriteAllBytes(Path.Combine(addon, "plugin.cfg"), "[plugin]\nname=\"thing\"\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(addon, "main.gd"), "extends Node\nfunc _ready():\n\tpass\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(addon, "lib.bin"), [0x7f, 0x45, 0x00, 0x0d, 0x0a, 0x00, 0x0d, 0x0a, 0x01]);

        Assert.Equal(0, Git(work, author, "add", "-A").Exit);
        Assert.Equal(0, Git(work, author, "commit", "--quiet", "-m", "fixture").Exit);
        Assert.Equal(0, Git(work, author, "push", "--quiet", "origin", "HEAD:main").Exit);
        return (remote, Git(work, author, "rev-parse", "HEAD").Out.Trim());
    }

    private string Checkout(Dictionary<string, string> machine, string remote, string sha, string name)
    {
        var dest = Path.Combine(_root, name);
        new ProcessGitClient(machine).FetchCommitAsync(remote, sha, dest).GetAwaiter().GetResult();
        return Path.Combine(dest, "addons", "thing");
    }

    [Fact]
    public void TheSameCommit_OnAWindowsStyleAndALinuxStyleMachine_HasDifferentBytesAndTheSameHash()
    {
        if (!GitAvailable()) return;
        var (remote, sha) = MakeRemote();

        var windows = Checkout(Machine("windows", "[core]\n\tautocrlf = true\n"), remote, sha, "dest-windows");
        var linux = Checkout(Machine("linux", ""), remote, sha, "dest-linux");

        // Precondition: the bug is real. These ARE different on disk, so a hash over the bytes would differ.
        var windowsText = File.ReadAllText(Path.Combine(windows, "main.gd"));
        var linuxText = File.ReadAllText(Path.Combine(linux, "main.gd"));
        Assert.Contains("\r\n", windowsText);
        Assert.DoesNotContain("\r\n", linuxText);

        var fs = new SystemFileSystem();
        Assert.Equal(ContentHasher.HashDirectory(fs, linux), ContentHasher.HashDirectory(fs, windows));
    }

    [Fact]
    public void AMachineThatChecksOutCrlfBecauseOfCoreEol_AgreesToo()
    {
        if (!GitAvailable()) return;
        var (remote, sha) = MakeRemote();

        // eol applies to files marked text; autocrlf=input leaves LF. Either way the hash must agree with plain LF.
        var eolCrlf = Checkout(Machine("eol", "[core]\n\teol = crlf\n\tautocrlf = true\n"), remote, sha, "dest-eol");
        var plain = Checkout(Machine("plain", ""), remote, sha, "dest-plain");

        var fs = new SystemFileSystem();
        Assert.Equal(ContentHasher.HashDirectory(fs, plain), ContentHasher.HashDirectory(fs, eolCrlf));
    }

    [Fact]
    public void ALockfileWrittenOnEitherMachine_IsCleanOnTheOther_AndNeverRewritten()
    {
        if (!GitAvailable()) return;
        var (remote, sha) = MakeRemote();
        var windows = Checkout(Machine("windows2", "[core]\n\tautocrlf = true\n"), remote, sha, "dest-w2");
        var linux = Checkout(Machine("linux2", ""), remote, sha, "dest-l2");
        var fs = new SystemFileSystem();

        // What the OLD dotquay recorded on each machine: the bytes as written there.
        var recordedOnWindows = ContentHasher.HashDirectoryIncludingArtifacts(fs, windows);
        var recordedOnLinux = ContentHasher.HashDirectoryIncludingArtifacts(fs, linux);
        Assert.NotEqual(recordedOnWindows, recordedOnLinux);

        Assert.False(DirtyChecker.Check(fs, linux, recordedOnWindows).IsDirty);
        Assert.False(DirtyChecker.Check(fs, windows, recordedOnLinux).IsDirty);

        // And a reinstall of the same commit keeps whichever value was recorded.
        var staging = Path.Combine(_root, "staging-linux");
        CopyDirectory(linux, staging);
        var kept = new SyncEngine(fs).SwapAndHash(staging, linux, recordedOnWindows, force: false);
        Assert.Equal(recordedOnWindows, kept);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
