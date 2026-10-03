using System.Diagnostics;
using System.Security.Cryptography;
using Dotquay.Core.Git;
using Xunit;

namespace Dotquay.Tests.Git;

/// <summary>
/// <see cref="ProcessGitClient"/> against a real repository whose binaries live in real Git LFS. Fakes cannot
/// stand in for this: whether the checkout holds files or placeholders is decided by git, by git-lfs, and by
/// the machine's global git configuration, and nothing but a real git run shows which.
/// </summary>
/// <remarks>
/// <para>
/// The machine states are made deterministic by pointing <c>GIT_CONFIG_GLOBAL</c> at a file the test
/// controls, rather than depending on whether the developer running the suite happens to have run
/// <c>git lfs install</c>: one file with the LFS filter in it (the activated machine), one empty (git-lfs
/// installed but never activated, which is how a fresh Linux box or CI image looks).
/// </para>
/// <para>
/// Returns early, passing, where git-lfs is not installed: the behaviour under test needs it.
/// </para>
/// </remarks>
public sealed class ProcessGitClientLfsRealProcessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotquay-lfs-real-").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort: a cleanup wrinkle must not fail a test whose assertions already passed.
        }
    }

    private static (int Exit, string Out, string Err) Git(string dir, IReadOnlyDictionary<string, string> env, params string[] args)
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
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, o.Result, e.Result);
    }

    private static bool LfsAvailable() => Git(Path.GetTempPath(), new Dictionary<string, string>(), "lfs", "version").Exit == 0;

    /// <summary>Environment for a machine whose global git config is the given file.</summary>
    private Dictionary<string, string> Machine(string name, bool lfsActivated)
    {
        var config = Path.Combine(_root, name + ".gitconfig");
        File.WriteAllText(config, "[user]\n\tname = t\n\temail = t@example.com\n[init]\n\tdefaultBranch = main\n");
        var env = new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = config, ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_TERMINAL_PROMPT"] = "0",
        };
        if (lfsActivated)
        {
            Assert.Equal(0, Git(_root, env, "lfs", "install", "--skip-repo").Exit);
        }

        return env;
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>A bare repository with one commit holding an LFS-tracked binary and a plain text file.</summary>
    private (string Remote, string Sha, string BinarySha256) MakeRemote(bool useLfs, bool pushLfsObjects = true)
    {
        var author = Machine("author", lfsActivated: true);
        var remote = Path.Combine(_root, "remote.git");
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(remote);
        Directory.CreateDirectory(work);
        Assert.Equal(0, Git(remote, author, "init", "--bare", "--quiet").Exit);
        Assert.Equal(0, Git(work, author, "init", "--quiet").Exit);
        Assert.Equal(0, Git(work, author, "remote", "add", "origin", remote).Exit);

        if (useLfs)
        {
            Assert.Equal(0, Git(work, author, "lfs", "track", "*.dll").Exit);
        }

        var binary = Path.Combine(work, "addons", "GDMP", "libs", "GDMP.windows.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        var bytes = new byte[1_500_000];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(binary, bytes);
        File.WriteAllText(Path.Combine(work, "addons", "GDMP", "plugin.cfg"), "[plugin]\nname=\"GDMP\"\n");

        Assert.Equal(0, Git(work, author, "add", "-A").Exit);
        Assert.Equal(0, Git(work, author, "commit", "--quiet", "-m", "fixture").Exit);

        var pushEnv = new Dictionary<string, string>(author);
        if (!pushLfsObjects)
        {
            pushEnv["GIT_LFS_SKIP_PUSH"] = "1"; // leaves the pointers in the commit and the objects off the server
        }

        Assert.Equal(0, Git(work, pushEnv, "push", "--quiet", "origin", "HEAD:main").Exit);
        var sha = Git(work, author, "rev-parse", "HEAD").Out.Trim();
        return (remote, sha, Sha256(binary));
    }

    private static ProcessGitClient ClientOn(Dictionary<string, string> machine) => new(machine);

    private string Fetch(ProcessGitClient client, string remote, string sha, string name)
    {
        var dest = Path.Combine(_root, name);
        client.FetchCommitAsync(remote, sha, dest).GetAwaiter().GetResult();
        return Path.Combine(dest, "addons", "GDMP", "libs", "GDMP.windows.dll");
    }

    [Fact]
    public void AMachineWithLfsActivated_GetsTheRealFiles()
    {
        if (!LfsAvailable()) return; // git-lfs absent: there is nothing to exercise (same convention as the other real-process tests)
        var (remote, sha, expected) = MakeRemote(useLfs: true);

        var installed = Fetch(ClientOn(Machine("active", lfsActivated: true)), remote, sha, "dest-active");

        Assert.Equal(expected, Sha256(installed));
    }

    [Fact]
    public void AMachineWithLfsInstalledButNeverActivated_IsRepaired_NotLeftWithPlaceholders()
    {
        // The bug this exists for. Without the repair this checkout leaves a 130-byte pointer text file,
        // and dotquay installed it, hashed it into the lockfile, and reported the addon clean.
        if (!LfsAvailable()) return; // git-lfs absent: there is nothing to exercise (same convention as the other real-process tests)
        var (remote, sha, expected) = MakeRemote(useLfs: true);
        var machine = Machine("inactive", lfsActivated: false);

        var installed = Fetch(ClientOn(machine), remote, sha, "dest-inactive");

        Assert.True(new FileInfo(installed).Length > 1_000_000, "the placeholder was replaced by the real file");
        Assert.Equal(expected, Sha256(installed));
    }

    [Fact]
    public void TheRepairNeverTouchesTheUsersGlobalGitConfig()
    {
        if (!LfsAvailable()) return; // git-lfs absent: there is nothing to exercise (same convention as the other real-process tests)
        var (remote, sha, _) = MakeRemote(useLfs: true);
        var machine = Machine("untouched", lfsActivated: false);
        var before = File.ReadAllText(machine["GIT_CONFIG_GLOBAL"]);

        Fetch(ClientOn(machine), remote, sha, "dest-untouched");

        Assert.Equal(before, File.ReadAllText(machine["GIT_CONFIG_GLOBAL"]));
        Assert.DoesNotContain("lfs", File.ReadAllText(machine["GIT_CONFIG_GLOBAL"]), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARepositoryWithoutLfs_IsUnaffected()
    {
        if (!LfsAvailable()) return; // git-lfs absent: there is nothing to exercise (same convention as the other real-process tests)
        var (remote, sha, expected) = MakeRemote(useLfs: false);

        var installed = Fetch(ClientOn(Machine("plain", lfsActivated: false)), remote, sha, "dest-plain");

        Assert.Equal(expected, Sha256(installed));
    }

    [Fact]
    public void AnLfsServerThatLacksTheFiles_FailsWithAMessage_NotWithPlaceholders()
    {
        if (!LfsAvailable()) return; // git-lfs absent: there is nothing to exercise (same convention as the other real-process tests)
        var (remote, sha, _) = MakeRemote(useLfs: true, pushLfsObjects: false);
        var machine = Machine("missing-objects", lfsActivated: false);

        var ex = Assert.ThrowsAny<GitCommandException>(() => Fetch(ClientOn(machine), remote, sha, "dest-missing"));

        Assert.Contains("Git LFS", ex.Message);
        Assert.Contains("GDMP.windows.dll", ex.Message);
    }

    [Fact]
    public void ACheckoutThatWasLeftWithPlaceholders_IsRepairedWhenVerifiedLater()
    {
        // The cache path: an entry made by an older dotquay is verified in place on its next use.
        if (!LfsAvailable()) return; // git-lfs absent: there is nothing to exercise (same convention as the other real-process tests)
        var (remote, sha, expected) = MakeRemote(useLfs: true);
        var inactive = Machine("legacy", lfsActivated: false);

        // Reproduce the old behaviour: a plain fetch and checkout with no repair step.
        var dest = Path.Combine(_root, "dest-legacy");
        Directory.CreateDirectory(dest);
        Assert.Equal(0, Git(dest, inactive, "init", "--quiet").Exit);
        Assert.Equal(0, Git(dest, inactive, "remote", "add", "origin", remote).Exit);
        Assert.Equal(0, Git(dest, inactive, "fetch", "--quiet", "--depth", "1", "origin", sha).Exit);
        Assert.Equal(0, Git(dest, inactive, "checkout", "--quiet", sha).Exit);
        var binary = Path.Combine(dest, "addons", "GDMP", "libs", "GDMP.windows.dll");
        Assert.True(new FileInfo(binary).Length < 1024, "precondition: the old behaviour leaves a placeholder");

        ClientOn(inactive).EnsureFilesResolvedAsync(dest).GetAwaiter().GetResult();

        Assert.Equal(expected, Sha256(binary));
    }
}
