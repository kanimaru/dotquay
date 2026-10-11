using System.Diagnostics;
using Dotquay.Core.Git;
using Xunit;

namespace Dotquay.Tests.Git;

/// <summary>
/// <see cref="ProcessGitClient.GetCommitDateAsync"/> against a real git: a commit with a known committer date,
/// shallow-fetched the way the cache fetches it, reads back as that date.
/// </summary>
/// <remarks>
/// Needs a real git: the fakes return whatever date they are given, so only this proves the format string and the
/// parse agree with what git prints, and that a depth-1 fetch still carries the commit's date. Returns early,
/// passing, without git (same convention as the other real-process tests).
/// </remarks>
public sealed class ProcessGitClientCommitDateRealProcessTests : IDisposable
{
    // 01:30 on 1 October at UTC+2: 23:30 on 30 September in UTC, so a local-time reading shows as the wrong day.
    private const string CommitterDate = "2026-10-01T01:30:00+02:00";

    private readonly string _root = Directory.CreateTempSubdirectory("dotquay-commitdate-real-").FullName;

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

    private (int Exit, string Out) Git(string dir, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        foreach (var (k, v) in Environment()) info.Environment[k] = v;
        using var p = Process.Start(info)!;
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, o.Result);
    }

    private Dictionary<string, string> Environment()
    {
        var config = Path.Combine(_root, "test.gitconfig");
        if (!File.Exists(config))
        {
            File.WriteAllText(config, "[user]\n\tname = t\n\temail = t@example.com\n");
        }

        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = config, ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_AUTHOR_DATE"] = CommitterDate, ["GIT_COMMITTER_DATE"] = CommitterDate,
        };
    }

    private bool GitAvailable()
    {
        try { return Git(Path.GetTempPath(), "--version").Exit == 0; }
        catch { return false; }
    }

    /// <summary>A bare repository with two commits, so the fetched one is not the root and depth 1 really cuts history.</summary>
    private (string Remote, string Sha) MakeRemote()
    {
        var remote = Path.Combine(_root, "remote.git");
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(remote);
        Directory.CreateDirectory(work);
        Assert.Equal(0, Git(remote, "init", "--bare", "--quiet").Exit);
        Assert.Equal(0, Git(work, "init", "--quiet").Exit);
        Assert.Equal(0, Git(work, "remote", "add", "origin", remote).Exit);

        File.WriteAllText(Path.Combine(work, "plugin.cfg"), "[plugin]\n");
        Assert.Equal(0, Git(work, "add", "-A").Exit);
        Assert.Equal(0, Git(work, "commit", "--quiet", "-m", "first").Exit);
        File.WriteAllText(Path.Combine(work, "main.gd"), "extends Node\n");
        Assert.Equal(0, Git(work, "add", "-A").Exit);
        Assert.Equal(0, Git(work, "commit", "--quiet", "-m", "second").Exit);
        Assert.Equal(0, Git(work, "push", "--quiet", "origin", "HEAD:main").Exit);
        return (remote, Git(work, "rev-parse", "HEAD").Out.Trim());
    }

    [Fact]
    public async Task AShallowFetchedCommit_ReadsBackItsCommitterDate()
    {
        if (!GitAvailable()) return;
        var (remote, sha) = MakeRemote();
        var client = new ProcessGitClient(Environment());
        var checkout = Path.Combine(_root, "checkout");
        await client.FetchCommitAsync(remote, sha, checkout);

        var date = await client.GetCommitDateAsync(checkout, sha);

        Assert.Equal(DateTimeOffset.Parse(CommitterDate), date);
        Assert.Equal(new DateTime(2026, 9, 30), date!.Value.UtcDateTime.Date);
    }

    [Fact]
    public async Task ACommitTheCheckoutDoesNotHold_HasNoDate()
    {
        if (!GitAvailable()) return;
        var (remote, sha) = MakeRemote();
        var client = new ProcessGitClient(Environment());
        var checkout = Path.Combine(_root, "checkout");
        await client.FetchCommitAsync(remote, sha, checkout);

        var date = await client.GetCommitDateAsync(checkout, new string('f', 40));

        // Null, not an exception: the label is display-only and must never fail an install.
        Assert.Null(date);
    }
}
