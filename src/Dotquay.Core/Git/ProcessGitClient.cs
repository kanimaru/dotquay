using System.ComponentModel;
using System.Diagnostics;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Diagnostics;
using Dotquay.Core.IO;

namespace Dotquay.Core.Git;

/// <summary>
/// Shells out to <c>git</c> on PATH (SPEC.md §12: real git handles SSH keys, credential helpers,
/// and proxies for free — LibGit2Sharp auth is a known pain, pure-library is a later refinement).
/// </summary>
/// <remarks>
/// Every child process gets the environment <see cref="GitEnvironment"/> builds: prompts disabled,
/// and dotquay appended as a credential helper *after* whatever the machine already has, so an
/// existing SSH agent or Git Credential Manager still answers first and dotquay only fills the gap
/// (SPEC.md §11.2).
/// </remarks>
public sealed class ProcessGitClient : IGitClient
{
    private readonly IReadOnlyDictionary<string, string> _childEnvironment;
    private readonly LfsResolver _lfs;

    /// <param name="childEnvironment">Extra variables for every git child process. Null builds the
    /// real one from <see cref="Environment.ProcessPath"/> — the AOT-safe way to find dotquay's own
    /// binary, since <c>Assembly.Location</c> is empty in a published NativeAOT image.</param>
    public ProcessGitClient(IReadOnlyDictionary<string, string>? childEnvironment = null)
    {
        _childEnvironment = childEnvironment ?? GitEnvironment.Build(Environment.ProcessPath, new SystemEnvironmentReader());
        _lfs = new LfsResolver((directory, args, token) => RunGitAsync(directory, args, token));
    }

    public async Task<IReadOnlyList<GitRef>> ListRemoteRefsAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(workingDirectory: null, ["ls-remote", "--tags", "--heads", repositoryUrl], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw Failure(
                GitErrorClassifier.DescribeRemoteFailure($"git ls-remote failed for {repositoryUrl}", repositoryUrl, result.StdErr),
                result.StdErr);
        }

        return ParseRefs(result.StdOut);
    }

    public async Task<string> ResolveBranchTipAsync(string repositoryUrl, string branch, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(workingDirectory: null, ["ls-remote", "--heads", repositoryUrl, branch], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw Failure(
                GitErrorClassifier.DescribeRemoteFailure(
                    $"git ls-remote failed for {repositoryUrl} branch '{branch}'", repositoryUrl, result.StdErr),
                result.StdErr);
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
            throw Failure($"git ls-remote --symref failed for {repositoryUrl}: {result.StdErr.Trim()}", result.StdErr);
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
                throw Failure(
                    GitErrorClassifier.DescribeFetchFailure(repositoryUrl, commitSha, shallow.StdErr, full.StdErr),
                    full.StdErr + shallow.StdErr);
            }
        }

        await RunGitOrThrowAsync(destinationPath, ["checkout", "--quiet", commitSha], cancellationToken, "git checkout");

        // A repository that keeps its binaries in Git LFS checks out as pointer text, not files, unless
        // the machine has git-lfs installed and activated, and git does not say so. See LfsResolver.
        await _lfs.EnsureResolvedAsync(destinationPath, "origin", cancellationToken);
    }

    public async Task<DateTimeOffset?> GetCommitDateAsync(
        string checkoutPath, string commitSha, CancellationToken cancellationToken = default)
    {
        // %ct is the committer date as Unix seconds: no time zone or locale to parse. A shallow
        // fetch still holds the commit object itself, so this works on every cache entry.
        var result = await RunGitAsync(checkoutPath, ["show", "--no-patch", "--format=%ct", commitSha], cancellationToken);

        return result.ExitCode == 0 && long.TryParse(result.StdOut.Trim(), out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    public Task EnsureFilesResolvedAsync(string checkoutPath, CancellationToken cancellationToken = default)
        => _lfs.EnsureResolvedAsync(checkoutPath, "origin", cancellationToken);

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

    /// <summary>Builds a <see cref="GitCommandException"/> that already knows whether git was
    /// complaining about credentials, so the CLI doesn't have to re-read the prose (SPEC.md §17).</summary>
    private static GitCommandException Failure(string message, string stderr) =>
        new(message, GitErrorClassifier.LooksLikeAuthenticationFailure(stderr));

    private async Task RunGitOrThrowAsync(
        string? workingDirectory, string[] args, CancellationToken cancellationToken, string description)
    {
        var result = await RunGitAsync(workingDirectory, args, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw Failure($"{description} failed: {result.StdErr.Trim()}", result.StdErr);
        }
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(
        string? workingDirectory, string[] args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Redirected even though git never reads it: leaving this false makes the child inherit
            // dotquay's own stdin handle. Under dotquay mcp, that handle is the anonymous pipe the
            // MCP host reads JSON-RPC requests from -- a console-less, fully-piped process spawning a
            // console child that way deadlocks the child before it can even print --version, hanging
            // every doctor/status/graph call that shells out to git. Closed immediately below so the
            // child sees a clean EOF instead of an open, unread handle.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Set on the child only. dotquay's own process environment is never mutated — several git
        // invocations can be in flight at once, and process-global state shared between them is the
        // bug class IEnvironmentReader exists to avoid.
        foreach (var (name, value) in _childEnvironment)
        {
            startInfo.Environment[name] = value;
        }

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = startInfo };
        var trace = ProcessTrace.Start("git", args, workingDirectory);

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            trace.Failed(ex);
            throw new GitCommandException("Could not start 'git'. Is it installed and on PATH?", ex);
        }

        process.StandardInput.Close();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            trace.Failed(ex);
            throw;
        }

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        trace.Completed(process.ExitCode, stdOut, stdErr);
        return (process.ExitCode, stdOut, stdErr);
    }
}
