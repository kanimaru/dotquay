using System.ComponentModel;
using System.Diagnostics;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Diagnostics;

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
/// header path (<c>a/dir1/relpath</c>, <c>b/dir2/relpath</c>), not an abstracted placeholder — so
/// the number of path segments to strip at apply time (<c>git apply -p&lt;N&gt;</c>) depends
/// entirely on what was passed at capture time. Fixing both names to always be single segments
/// makes that number a known constant (2: one for the <c>a/</c>/<c>b/</c> prefix itself, one for
/// the fixed directory name) regardless of the real, arbitrarily-nested caller paths — capture and
/// apply can happen on completely different machines (a patch is captured on one developer's
/// machine, re-applied on a teammate's during <c>install</c>) with no shared filesystem layout to
/// rely on otherwise.
/// </remarks>
public sealed class ProcessPatchTool : IPatchTool {
    public async Task<string> DiffDirectoriesAsync(
        string baseDirectory, string oursDirectory, CancellationToken cancellationToken = default) {
        var parent = Path.Combine(Path.GetTempPath(), "dotquay-diff-" + Guid.NewGuid()
            .ToString("N"));
        var baseAlias = Path.Combine(parent, "base");
        var oursAlias = Path.Combine(parent, "ours");

        try {
            // Real copies, not symlinks/junctions — keeps this fully cross-platform (SPEC.md §12
            // targets win/linux/mac from one codebase) at the cost of a copy. Addon folders are
            // small enough (a few MB at most) that this is a non-issue for an occasional,
            // user-triggered `dotquay patch` call, not a hot path.
            //
            // Text files are copied with LF line endings (see LineEndings), so the patch never
            // records how the two trees happened to be checked out, only what changed.
            CopyDirectory(baseDirectory, baseAlias, toLf: true);
            CopyDirectory(oursDirectory, oursAlias, toLf: true);

            var result = await RunGitAsync(parent,
                ["-c", "core.autocrlf=false", "diff", "--no-index", "--no-color", "--binary", "--",
                    "base", "ours"],
                cancellationToken);

            // git diff --no-index: exit 0 = identical, exit 1 = differences found (NOT an error —
            // this is the whole point of the call), anything else = a real failure.
            if (result.ExitCode is not (0 or 1)) {
                throw new PatchException($"git diff --no-index failed (exit {result.ExitCode}): "
                    + $"{result.StdErr.Trim()}");
            }

            return result.StdOut;
        } finally {
            TryDeleteDirectory(parent);
        }
    }

    public async Task ApplyPatchAsync(string patchText, string targetDirectory,
        CancellationToken cancellationToken = default) {
        if (string.IsNullOrEmpty(patchText)) {
            return; // nothing to apply — an empty patch means "no changes", not an error
        }

        var patchFile = Path.Combine(Path.GetTempPath(), "dotquay-patch-" + Guid.NewGuid()
            .ToString("N") + ".patch");

        try {
            // Must preserve the diff text's own line endings exactly (git's own control lines —
            // "diff --git", "@@" hunks — are LF-terminated even on Windows; File.WriteAllText does
            // not rewrite embedded line endings, so this is safe as long as nothing upstream of
            // here ran the text through anything that normalizes them, e.g.
            // Environment.NewLine-based string building). Confirmed empirically: round-tripping
            // through a line-ending-mangling path (PowerShell's own `>` redirection, when
            // hand-verifying this exact mechanism) produces git's literal "No valid patches in
            // input" error.
            //
            // CRLF in the text itself is folded to LF: the target is applied to as an LF view
            // (below), and a patch captured before capture normalised may carry CRLF lines.
            await File.WriteAllTextAsync(patchFile, LineEndings.ToLf(patchText),
                cancellationToken);

            // -p2 strips exactly "a/base/" or "b/ours/" (the fixed prefix DiffDirectoriesAsync
            // always produces — see its remarks), leaving the bare relative path, which git
            // resolves against the process's working directory — so running with
            // CWD=targetDirectory applies the patch straight into it. Deliberately not
            // `--directory=<targetDirectory>` instead: confirmed against real git on Windows that
            // passing an absolute, backslash-separated target there produces "error: invalid path"
            // (git's path-prefix concatenation doesn't handle it), whereas CWD-based resolution is
            // both simpler and portable.
            //
            // The target must not count as a subdirectory of some enclosing repository: staging
            // lives under <project>/.dotquay/, and the project is usually a git repo itself. Run
            // from a subdirectory of a work tree, `git apply` reads the patch's paths relative to
            // that repository's top and silently skips every one outside the current directory,
            // exiting 0 with nothing applied. A ceiling at the target's parent stops git's
            // repository discovery there, so the patch applies as to a plain folder.
            //
            // The patch holds LF, but the target may be checked out with CRLF. Its text files are
            // turned to LF for the apply and each changed file is written back in its own style
            // afterwards (LineEndings), so neither the checkout nor core.autocrlf decides whether
            // the hunks match.
            var originals = ToLfView(targetDirectory);
            var result = await RunGitAsync(
                workingDirectory: targetDirectory,
                ["-c", "core.autocrlf=false", "apply", "-p2", patchFile], cancellationToken,
                ceilingDirectory: Path.GetDirectoryName(Path.GetFullPath(targetDirectory)));

            if (result.ExitCode == 0) {
                RestoreLineEndings(targetDirectory, originals);
            } else {
                RestoreOriginals(targetDirectory, originals);
            }

            if (result.ExitCode != 0) {
                throw new PatchException(
                    $"'git apply' failed to reconstruct the patched addon (exit "
                    + $"{result.ExitCode}): {result.StdErr.Trim()}. "
                    + "The patch file or cache may be out of sync — try 'dotquay patch <name>' "
                    + "again once the conflict is resolved.");
            }
        } finally {
            TryDelete(patchFile);
        }
    }

    public async Task<int> MergeFileAsync(
        string currentPath, string basePath, string otherPath,
        CancellationToken cancellationToken = default) {
        var result = await RunGitAsync(
            workingDirectory: null,
            ["merge-file", "-L", "current", "-L", "base", "-L", "other", currentPath, basePath,
                otherPath],
            cancellationToken);

        // git merge-file's exit code IS the conflict count (0 = clean); negative means a real error
        // (e.g. couldn't open one of the files) rather than a mergeable-but-conflicting result.
        if (result.ExitCode < 0) {
            throw new PatchException($"'git merge-file' failed (exit {result.ExitCode}): "
                + $"{result.StdErr.Trim()}");
        }

        return result.ExitCode;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory,
        bool toLf) {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*",
            SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var destination = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (!toLf) {
                File.Copy(file, destination, overwrite: true);
                continue;
            }

            var content = File.ReadAllBytes(file);
            File.WriteAllBytes(destination,
                LineEndings.IsText(content) ? LineEndings.ToLf(content) : content);
        }
    }

    /// <summary>Snapshots every text file of <paramref name="directory"/> by relative path and
    /// rewrites those holding CRLF with LF, so an LF patch can match them.</summary>
    private static Dictionary<string, byte[]> ToLfView(string directory) {
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(directory, "*",
            SearchOption.AllDirectories)) {
            var content = File.ReadAllBytes(file);
            if (!LineEndings.IsText(content)) {
                continue;
            }

            originals[Path.GetRelativePath(directory, file)] = content;
            if (LineEndings.HasCrlf(content)) {
                File.WriteAllBytes(file, LineEndings.ToLf(content));
            }
        }

        return originals;
    }

    /// <summary>After a successful apply: files the patch left alone get their exact original
    /// bytes back, changed files their original's line endings, and new text files the style most
    /// of the tree's text files use.</summary>
    private static void RestoreLineEndings(string directory,
        IReadOnlyDictionary<string, byte[]> originals) {
        var crlfFiles = originals.Values.Count(v => LineEndings.Detect(v)
            == LineEndings.Style.Crlf);
        var treeStyle = crlfFiles * 2 > originals.Count ? LineEndings.Style.Crlf
            : LineEndings.Style.Lf;

        foreach (var file in Directory.EnumerateFiles(directory, "*",
            SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(directory, file);
            var current = File.ReadAllBytes(file);

            if (originals.TryGetValue(relative, out var original)) {
                if (LineEndings.ToLf(original).AsSpan().SequenceEqual(current)) {
                    if (!original.AsSpan().SequenceEqual(current)) {
                        File.WriteAllBytes(file, original);
                    }

                    continue;
                }

                WriteIfDifferent(file, current,
                    LineEndings.To(LineEndings.Detect(original), current));
            } else if (LineEndings.IsText(current)) {
                WriteIfDifferent(file, current, LineEndings.To(treeStyle, current));
            }
        }
    }

    /// <summary>After a failed apply: every file turned to LF gets its original bytes back.</summary>
    private static void RestoreOriginals(string directory,
        IReadOnlyDictionary<string, byte[]> originals) {
        foreach (var (relative, original) in originals) {
            var path = Path.Combine(directory, relative);
            if (LineEndings.HasCrlf(original) && File.Exists(path)) {
                File.WriteAllBytes(path, original);
            }
        }
    }

    private static void WriteIfDifferent(string path, byte[] current, byte[] wanted) {
        if (!current.AsSpan().SequenceEqual(wanted)) {
            File.WriteAllBytes(path, wanted);
        }
    }

    private static void TryDeleteDirectory(string path) {
        try {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        } catch (IOException) {
            // Best-effort cleanup — a leftover temp copy is harmless clutter, not worth failing the
            // whole patch operation over.
        }
    }

    private static void TryDelete(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        } catch (IOException) {
            // Best-effort cleanup, same rationale as TryDeleteDirectory.
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(
        string? workingDirectory, string[] args, CancellationToken cancellationToken,
        string? ceilingDirectory = null) {
        var startInfo = new ProcessStartInfo("git") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args) {
            startInfo.ArgumentList.Add(arg);
        }

        if (workingDirectory is not null) {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (ceilingDirectory is not null) {
            // An inherited GIT_DIR or GIT_WORK_TREE (dotquay run from a git hook, say) would
            // bypass discovery and the ceiling with it.
            startInfo.Environment.Remove("GIT_DIR");
            startInfo.Environment.Remove("GIT_WORK_TREE");
            startInfo.Environment["GIT_CEILING_DIRECTORIES"] = ceilingDirectory;
        }

        using var process = new Process { StartInfo = startInfo };
        var trace = ProcessTrace.Start("git", args, workingDirectory, category: "patch");

        try {
            process.Start();
        } catch (Win32Exception ex) {
            trace.Failed(ex);
            throw new PatchException("Could not start 'git'. Is it installed and on PATH?", ex);
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        trace.Completed(process.ExitCode, stdOut, stdErr);
        return (process.ExitCode, stdOut, stdErr);
    }
}
