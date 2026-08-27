using System.ComponentModel;
using System.Diagnostics;
using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Patch;

/// <summary>
/// Shells out to <c>git diff --no-index</c>/<c>git apply</c>/<c>git merge-file</c> (SPEC.md §10,
/// requirement P1-1) — same "let the real tool do the real work" philosophy as
/// <see cref="Git.ProcessGitClient"/>/<see cref="Dotnet.ProcessDotnetCliRunner"/>.
/// </summary>
/// <remarks>
/// <see cref="DiffDirectoriesAsync"/> always names its two comparison roots with fixed one-segment
/// relative names ("base"/"ours", copied into a fresh temp parent and run from there) rather than
/// passing whatever caller-supplied paths through directly. This is deliberate, confirmed against
/// real git: <c>git diff --no-index dir1 dir2</c> embeds the *actual argument text* as part of each
/// header path (<c>a/dir1/relpath</c>, <c>b/dir2/relpath</c>), not an abstracted placeholder — so the
/// number of path segments to strip at apply time (<c>git apply -p&lt;N&gt;</c>) depends entirely on
/// what was passed at capture time. Fixing both names to always be single segments makes that number
/// a known constant (2: one for the <c>a/</c>/<c>b/</c> prefix itself, one for the fixed directory
/// name) regardless of the real, arbitrarily-nested caller paths — capture and apply can happen on
/// completely different machines (a patch is captured on one developer's machine, re-applied on a
/// teammate's during <c>install</c>) with no shared filesystem layout to rely on otherwise.
/// </remarks>
public sealed class ProcessPatchTool : IPatchTool
{
    public async Task<string> DiffDirectoriesAsync(
        string baseDirectory, string oursDirectory, CancellationToken cancellationToken = default)
    {
        var parent = Path.Combine(Path.GetTempPath(), "dotquay-diff-" + Guid.NewGuid().ToString("N"));
        var baseAlias = Path.Combine(parent, "base");
        var oursAlias = Path.Combine(parent, "ours");

        try
        {
            // Real copies, not symlinks/junctions — keeps this fully cross-platform (SPEC.md §12
            // targets win/linux/mac from one codebase) at the cost of a copy. Addon folders are
            // small enough (a few MB at most) that this is a non-issue for an occasional,
            // user-triggered `dotquay patch` call, not a hot path.
            CopyDirectory(baseDirectory, baseAlias);
            CopyDirectory(oursDirectory, oursAlias);

            var result = await RunGitAsync(parent, ["diff", "--no-index", "--no-color", "--binary", "--", "base", "ours"], cancellationToken);

            // git diff --no-index: exit 0 = identical, exit 1 = differences found (NOT an error —
            // this is the whole point of the call), anything else = a real failure.
            if (result.ExitCode is not (0 or 1))
            {
                throw new PatchException($"git diff --no-index failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
            }

            return result.StdOut;
        }
        finally
        {
            TryDeleteDirectory(parent);
        }
    }

    public async Task ApplyPatchAsync(string patchText, string targetDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(patchText))
        {
            return; // nothing to apply — an empty patch means "no changes", not an error
        }

        var patchFile = Path.Combine(Path.GetTempPath(), "dotquay-patch-" + Guid.NewGuid().ToString("N") + ".patch");

        try
        {
            // Must preserve the diff text's own line endings exactly (git's own control lines —
            // "diff --git", "@@" hunks — are LF-terminated even on Windows; File.WriteAllText does
            // not rewrite embedded line endings, so this is safe as long as nothing upstream of here
            // ran the text through anything that normalizes them, e.g. Environment.NewLine-based
            // string building). Confirmed empirically: round-tripping through a line-ending-mangling
            // path (PowerShell's own `>` redirection, when hand-verifying this exact mechanism)
            // produces git's literal "No valid patches in input" error.
            await File.WriteAllTextAsync(patchFile, patchText, cancellationToken);

            // -p2 strips exactly "a/base/" or "b/ours/" (the fixed prefix DiffDirectoriesAsync always
            // produces — see its remarks), leaving the bare relative path, which git resolves against
            // the process's working directory — so running with CWD=targetDirectory applies the
            // patch straight into it. Deliberately not `--directory=<targetDirectory>` instead:
            // confirmed against real git on Windows that passing an absolute, backslash-separated
            // target there produces "error: invalid path" (git's path-prefix concatenation doesn't
            // handle it), whereas CWD-based resolution is both simpler and portable.
            var result = await RunGitAsync(
                workingDirectory: targetDirectory, ["apply", "-p2", patchFile], cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new PatchException(
                    $"'git apply' failed to reconstruct the patched addon (exit {result.ExitCode}): {result.StdErr.Trim()}. "
                    + "The patch file or cache may be out of sync — try 'dotquay patch <name>' again once the conflict is resolved.");
            }
        }
        finally
        {
            TryDelete(patchFile);
        }
    }

    public async Task<int> MergeFileAsync(
        string currentPath, string basePath, string otherPath, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(
            workingDirectory: null,
            ["merge-file", "-L", "current", "-L", "base", "-L", "other", currentPath, basePath, otherPath],
            cancellationToken);

        // git merge-file's exit code IS the conflict count (0 = clean); negative means a real error
        // (e.g. couldn't open one of the files) rather than a mergeable-but-conflicting result.
        if (result.ExitCode < 0)
        {
            throw new PatchException($"'git merge-file' failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
        }

        return result.ExitCode;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var destination = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup — a leftover temp copy is harmless clutter, not worth failing the
            // whole patch operation over.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup, same rationale as TryDeleteDirectory.
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
            throw new PatchException("Could not start 'git'. Is it installed and on PATH?", ex);
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return (process.ExitCode, stdOut, stdErr);
    }
}
