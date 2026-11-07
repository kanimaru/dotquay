using System.Text;
using Dotquay.Core.Git;
using Xunit;

namespace Dotquay.Tests.Git;

/// <summary>
/// Every branch of <see cref="LfsResolver"/>, with git scripted: no repository, network or git-lfs needed.
/// The same logic against a real repository is in <see cref="ProcessGitClientLfsRealProcessTests"/>.
/// </summary>
public class LfsResolverTests {
    private const string Oid = "968c222503ef1c9e27e1071361187c210ee72457ee6c6afebfd46aa845adca77";
    private static readonly string Pointer = $"version https://git-lfs.github.com/spec/v1\noid sha256:{Oid}\nsize 2500000\n";

    /// <summary>A scripted git: answers by the arguments it is given and records every call.</summary>
    private sealed class ScriptedGit {
        public readonly List<string> Calls = [];
        public string[] PointerPaths = [];
        public bool LfsInstalled = true;
        public int InstallExit, PullExit;
        public string PullStdErr = "";
        public bool PullFixesFiles = true;
        public bool GrepFails;

        /// <summary>Paths git grep reports as pointers right now; emptied by a successful pull.</summary>
        private string[] _current = [];
        public Dictionary<string, byte[]> Files = new();

        public void Reset() { _current = PointerPaths; }

        public Task<(int ExitCode, string StdOut, string StdErr)> Run(string dir, string[] args, CancellationToken ct) {
            Calls.Add(string.Join(' ', args.Take(3)));
            switch (args[0]) {
                case "grep":
                    if (GrepFails) return Task.FromResult((2, "", "fatal: not a git repository"));
                    return _current.Length == 0
                        ? Task.FromResult((1, "", ""))
                        : Task.FromResult((0, string.Join('\0', _current) + "\0", ""));
                case "lfs" when args[1] == "version":
                    return Task.FromResult(LfsInstalled ? (0, "git-lfs/3.6.1", "") : (1, "", "git: 'lfs' is not a git command."));
                case "lfs" when args[1] == "install":
                    return Task.FromResult((InstallExit, "", InstallExit == 0 ? "" : "install failed"));
                case "lfs" when args[1] == "pull":
                    if (PullExit == 0 && PullFixesFiles) _current = [];
                    return Task.FromResult((PullExit, "", PullStdErr));
            }
            throw new InvalidOperationException($"unexpected git call: {string.Join(' ', args)}");
        }
    }

    private static LfsResolver Resolver(ScriptedGit git) {
        git.Reset();
        // The pointer files "exist" with real pointer content, so the file confirmation passes. The
        // length guard reads the real file system, so write real small files under a temp directory.
        return new LfsResolver(git.Run, path => git.Files.TryGetValue(Path.GetFileName(path), out var b) ? b : Encoding.UTF8.GetBytes(Pointer));
    }

    private static string TempCheckout(params string[] pointerNames) {
        var dir = Directory.CreateTempSubdirectory("dotquay-lfs-unit-").FullName;
        foreach (var name in pointerNames) {
            var full = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Pointer);
        }
        return dir;
    }

    [Fact]
    public async Task ARepositoryWithoutPointers_CostsOneSearchAndNothingElse() {
        var git = new ScriptedGit();
        var dir = TempCheckout();

        await Resolver(git).EnsureResolvedAsync(dir, "origin");

        Assert.Single(git.Calls);
        Assert.StartsWith("grep", git.Calls[0]);
    }

    [Fact]
    public async Task APointerWithGitLfsMissing_StopsWithAMessageThatNamesTheFix() {
        var git = new ScriptedGit { PointerPaths = ["addons/GDMP/libs/x.dll"], LfsInstalled = false };
        var dir = TempCheckout("addons/GDMP/libs/x.dll");

        var ex = await Assert.ThrowsAsync<GitCommandException>(() => Resolver(git).EnsureResolvedAsync(dir, "origin"));

        Assert.Contains("git-lfs is not installed", ex.Message);
        Assert.Contains("git lfs install", ex.Message);
        Assert.Contains("addons/GDMP/libs/x.dll", ex.Message);
        Assert.DoesNotContain(git.Calls, c => c.StartsWith("lfs pull"));
        Assert.False(ex.IsAuthenticationFailure);
    }

    [Fact]
    public async Task APointerWithGitLfsPresentButNotActivated_ActivatesItLocallyAndPulls() {
        var git = new ScriptedGit { PointerPaths = ["a.dll", "b.so"] };
        var dir = TempCheckout("a.dll", "b.so");

        await Resolver(git).EnsureResolvedAsync(dir, "origin");

        // Activate for THIS checkout only, never the user's global git config; then pull; then look again.
        Assert.Equal(["grep -l -I", "lfs version", "lfs install --local", "lfs pull origin", "grep -l -I"], git.Calls);
    }

    [Fact]
    public async Task AnActivationThatFails_StopsBeforePulling_WithGitsOwnReason() {
        var git = new ScriptedGit { PointerPaths = ["a.dll"], InstallExit = 1 };
        var dir = TempCheckout("a.dll");

        var ex = await Assert.ThrowsAsync<GitCommandException>(() => Resolver(git).EnsureResolvedAsync(dir, "origin"));

        Assert.Contains("install failed", ex.Message);
        Assert.DoesNotContain(git.Calls, c => c.StartsWith("lfs pull"));
    }

    [Fact]
    public async Task APullThatFails_SaysSoWithGitsOwnReason_AndFlagsAuthentication() {
        var git = new ScriptedGit {
            PointerPaths = ["a.dll"],
            PullExit = 1,
            PullStdErr = "batch response: Authorization error: ssh://git@host/x.git",
        };
        var dir = TempCheckout("a.dll");

        var ex = await Assert.ThrowsAsync<GitCommandException>(() => Resolver(git).EnsureResolvedAsync(dir, "origin"));

        Assert.Contains("downloading them failed", ex.Message);
        Assert.Contains("Authorization error", ex.Message);
    }

    [Fact]
    public async Task APullThatSucceedsButLeavesPointers_IsStillAnError() {
        // An LFS server that answers but lacks the objects can exit 0 and leave the placeholders.
        var git = new ScriptedGit { PointerPaths = ["a.dll"], PullFixesFiles = false };
        var dir = TempCheckout("a.dll");

        var ex = await Assert.ThrowsAsync<GitCommandException>(() => Resolver(git).EnsureResolvedAsync(dir, "origin"));

        Assert.Contains("still placeholders", ex.Message);
        Assert.Contains("git lfs push --all", ex.Message);
    }

    [Fact]
    public async Task ASearchThatCannotRun_NeverTurnsAWorkingInstallIntoAFailingOne() {
        // The safeguard is not a gate: if git grep itself breaks, there is nothing to act on.
        var git = new ScriptedGit { GrepFails = true };
        var dir = TempCheckout();

        await Resolver(git).EnsureResolvedAsync(dir, "origin");

        Assert.Single(git.Calls);
    }

    [Fact]
    public async Task AFileThatGrepFlagsButIsNotAPointer_IsLeftAlone() {
        // A short text file that merely begins with the version line, no oid: grep matches, the check does not.
        var dir = Directory.CreateTempSubdirectory("dotquay-lfs-unit-").FullName;
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "version https://git-lfs.github.com/spec/v1\n");
        var git = new ScriptedGit { PointerPaths = ["notes.txt"] };
        git.Files["notes.txt"] = Encoding.UTF8.GetBytes("version https://git-lfs.github.com/spec/v1\n");

        await Resolver(git).EnsureResolvedAsync(dir, "origin");

        Assert.Single(git.Calls); // no lfs version / install / pull
    }

    [Fact]
    public async Task AFileLargerThanAPointer_IsNeverReadAsOne() {
        var dir = Directory.CreateTempSubdirectory("dotquay-lfs-unit-").FullName;
        File.WriteAllText(Path.Combine(dir, "changelog.md"), Pointer + new string('x', 5000));
        var git = new ScriptedGit { PointerPaths = ["changelog.md"] };

        await Resolver(git).EnsureResolvedAsync(dir, "origin");

        Assert.Single(git.Calls);
    }
}
