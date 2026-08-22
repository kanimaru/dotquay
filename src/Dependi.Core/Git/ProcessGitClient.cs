using System.ComponentModel;
using System.Diagnostics;
using Dependi.Core.Abstractions;

namespace Dependi.Core.Git;

/// <summary>
/// Shells out to <c>git</c> on PATH (SPEC.md §12: real git handles SSH keys, credential helpers,
/// and proxies for free — LibGit2Sharp auth is a known pain, pure-library is a later refinement).
/// </summary>
public sealed class ProcessGitClient : IGitClient
{
    public async Task<IReadOnlyList<GitRef>> ListRemoteRefsAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(workingDirectory: null, ["ls-remote", "--tags", "--heads", repositoryUrl], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new GitCommandException($"git ls-remote failed for {repositoryUrl}: {result.StdErr.Trim()}");
        }

        return ParseRefs(result.StdOut);
    }

    public async Task<string> ResolveBranchTipAsync(string repositoryUrl, string branch, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(workingDirectory: null, ["ls-remote", "--heads", repositoryUrl, branch], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new GitCommandException($"git ls-remote failed for {repositoryUrl} branch '{branch}': {result.StdErr.Trim()}");
        }

        var refs = ParseRefs(result.StdOut);
        var match = refs.FirstOrDefault(r => r.Kind == GitRefKind.Branch && r.Name == branch);
        if (match is null)
        {
            throw new GitCommandException($"Branch '{branch}' not found on {repositoryUrl}.");
        }

        return match.CommitSha;
    }

    public async Task<string> ResolveDefaultBranchAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(workingDirectory: null, ["ls-remote", "--symref", repositoryUrl, "HEAD"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new GitCommandException($"git ls-remote --symref failed for {repositoryUrl}: {result.StdErr.Trim()}");
        }

        // Expected first line: "ref: refs/heads/main\tHEAD"
        var firstLine = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("ref: refs/heads/", StringComparison.Ordinal));

        if (firstLine is null)
        {
            throw new GitCommandException($"Could not determine the default branch for {repositoryUrl} (no symref in ls-remote output).");
        }

        var afterPrefix = firstLine["ref: refs/heads/".Length..];
        var tabIndex = afterPrefix.IndexOf('\t');
        return (tabIndex < 0 ? afterPrefix : afterPrefix[..tabIndex]).Trim();
    }

    public async Task FetchCommitAsync(
        string repositoryUrl,
        string commitSha,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationPath);

        await RunGitOrThrowAsync(destinationPath, ["init", "--quiet"], cancellationToken, "git init");
        await RunGitOrThrowAsync(destinationPath, ["remote", "add", "origin", repositoryUrl], cancellationToken, "git remote add");

        // Shallow fetch by SHA is the baseline, not an optimization (SPEC.md §9 step 1, §12 OQ-4).
        // Fall back to a full fetch only when the server refuses to shallow-fetch that exact commit
        // (older/self-hosted servers without uploadpack.allowReachableSHA1InWant).
        var shallow = await RunGitAsync(destinationPath, ["fetch", "--quiet", "--depth", "1", "origin", commitSha], cancellationToken);
        if (shallow.ExitCode != 0)
        {
            var full = await RunGitAsync(destinationPath, ["fetch", "--quiet", "origin"], cancellationToken);
            if (full.ExitCode != 0)
            {
                throw new GitCommandException(
                    $"Failed to fetch {commitSha} from {repositoryUrl}. "
                    + $"Shallow fetch: {shallow.StdErr.Trim()}. Full fetch: {full.StdErr.Trim()}");
            }
        }

        await RunGitOrThrowAsync(destinationPath, ["checkout", "--quiet", commitSha], cancellationToken, "git checkout");
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // RunGitAsync itself throws GitCommandException when the process can't even start
            // (Win32Exception — "git" not found on PATH at all); a non-zero exit from a `git`
            // that DID start is vanishingly unlikely for `--version`, but treated the same way
            // either way, "not available" is "not available".
            var result = await RunGitAsync(workingDirectory: null, ["--version"], cancellationToken);
            return result.ExitCode == 0;
        }
        catch (GitCommandException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses <c>git ls-remote</c> output into short-named refs. Annotated tags produce two lines
    /// for the same ref — the tag-object line and a peeled <c>^{}</c> line pointing at the commit
    /// it tags — and the peeled commit SHA is what callers actually want, so it always wins over
    /// the tag object's own SHA when both are present for the same name.
    /// </summary>
    internal static IReadOnlyList<GitRef> ParseRefs(string lsRemoteOutput)
    {
        var byName = new Dictionary<string, (string Sha, GitRefKind Kind)>(StringComparer.Ordinal);

        foreach (var rawLine in lsRemoteOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var tabIndex = line.IndexOf('\t');
            if (tabIndex < 0)
            {
                continue;
            }

            var sha = line[..tabIndex];
            var refName = line[(tabIndex + 1)..];

            var isPeeled = refName.EndsWith("^{}", StringComparison.Ordinal);
            if (isPeeled)
            {
                refName = refName[..^3];
            }

            string shortName;
            GitRefKind kind;
            if (refName.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                shortName = refName["refs/tags/".Length..];
                kind = GitRefKind.Tag;
            }
            else if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                shortName = refName["refs/heads/".Length..];
                kind = GitRefKind.Branch;
            }
            else
            {
                continue;
            }

            if (isPeeled || !byName.ContainsKey(shortName))
            {
                byName[shortName] = (sha, kind);
            }
        }

        return byName.Select(kv => new GitRef(kv.Key, kv.Value.Sha, kv.Value.Kind)).ToList();
    }

    private static async Task RunGitOrThrowAsync(
        string? workingDirectory, string[] args, CancellationToken cancellationToken, string description)
    {
        var result = await RunGitAsync(workingDirectory, args, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new GitCommandException($"{description} failed: {result.StdErr.Trim()}");
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(
        string? workingDirectory, string[] args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new GitCommandException("Could not start 'git'. Is it installed and on PATH?", ex);
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return (process.ExitCode, stdOut, stdErr);
    }
}
