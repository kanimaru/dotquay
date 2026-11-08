using System.Diagnostics;
using Dotquay.Core.IO;
using Dotquay.Core.Patch;
using Xunit;

namespace Dotquay.Tests.Patch;

/// <summary>
/// Real-git regression tests for <see cref="ProcessPatchTool.ApplyPatchAsync"/>. The staging
/// trees it applies into live under <c>&lt;project&gt;/.dotquay/staging/</c>, and a Godot project
/// is almost always a git repository itself. <c>git apply</c> run from a subdirectory of a work
/// tree reads the patch's paths relative to the repository's top and silently skips every path
/// outside the current directory, exiting 0. So a patch reapplied during <c>install</c> or
/// <c>update</c> vanished without a word: "ours" stayed equal to the pristine base, the 3-way
/// merge then (correctly) produced upstream unchanged, and the lockfile recorded the unpatched
/// content hash. Fakes cannot see this; it needs a real git and a real enclosing repository.
/// </summary>
public sealed class ProcessPatchToolRealGitTests {
    private const string RelativeScript = "scripts/camera.gd";
    private const string Original = "func a():\n\tpass\n\nfunc b():\n\tpass\n";
    private const string Patched = "func a():\n\tpass\n\nfunc patched():\n\tpass\n\nfunc b():\n"
        + "\tpass\n";

    [Fact]
    public async Task ApplyPatchAsync_TargetInsideAnotherRepository_AppliesThePatch() {
        var root = Directory.CreateTempSubdirectory("dotquay-patch-apply-").FullName;

        try {
            var tool = new ProcessPatchTool();
            var patchText = await CapturePatchAsync(tool, root);

            // The consumer project: a git repository with the staging tree nested inside it.
            var project = Path.Combine(root, "project");
            Directory.CreateDirectory(project);
            await RunGitAsync(project, "init", "-q");
            var target = Path.Combine(project, ".dotquay", "staging", "camera-patch-ours");
            WriteFile(target, RelativeScript, Original);

            await tool.ApplyPatchAsync(patchText, target);

            Assert.Equal(Patched, ReadNormalized(Path.Combine(target, RelativeScript)));
        } finally {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Reapply_BaseEqualsNewVersionInsideAnotherRepository_KeepsThePatch() {
        // The reported case: patch_base_sha equal to the resolved commit, so "theirs" is the
        // pristine base and the merge has to come out as exactly "ours".
        var root = Directory.CreateTempSubdirectory("dotquay-patch-merge-").FullName;

        try {
            var tool = new ProcessPatchTool();
            var patchText = await CapturePatchAsync(tool, root);

            var project = Path.Combine(root, "project");
            Directory.CreateDirectory(project);
            await RunGitAsync(project, "init", "-q");
            var staging = Path.Combine(project, ".dotquay", "staging");
            var oldBase = Path.Combine(staging, "camera-patch-old-base");
            var ours = Path.Combine(staging, "camera-patch-ours");
            var theirs = Path.Combine(staging, "camera");
            var merged = Path.Combine(staging, "camera-patch-merged");
            WriteFile(oldBase, RelativeScript, Original);
            WriteFile(ours, RelativeScript, Original);
            WriteFile(theirs, RelativeScript, Original);

            await tool.ApplyPatchAsync(patchText, ours);
            var fileSystem = new SystemFileSystem();
            var result = await new PatchMerger(fileSystem, tool)
                .MergeAsync(oldBase, ours, theirs, merged);

            Assert.Empty(result.ConflictedPaths);
            Assert.Equal(Patched, ReadNormalized(Path.Combine(merged, RelativeScript)));
        } finally {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>Captures the patch the way <c>dotquay patch</c> does, from two trees outside any
    /// repository's influence (the tool diffs copies in the system temp folder).</summary>
    private static async Task<string> CapturePatchAsync(ProcessPatchTool tool, string root) {
        var captureBase = Path.Combine(root, "capture-base");
        var captureOurs = Path.Combine(root, "capture-ours");
        WriteFile(captureBase, RelativeScript, Original);
        WriteFile(captureOurs, RelativeScript, Patched);

        var patchText = await tool.DiffDirectoriesAsync(captureBase, captureOurs);
        Assert.Contains("+func patched():", patchText);
        return patchText;
    }

    /// <summary>Reads a file with CRLF folded to LF: <c>git apply</c> writes through the user's
    /// <c>core.autocrlf</c>, which is not what these tests are about.</summary>
    private static string ReadNormalized(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n");

    private static void WriteFile(string directory, string relativePath, string content) {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(content));
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args) {
        var startInfo = new ProcessStartInfo("git") {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args) {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var stdErr = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {await stdErr}");
    }

    private static void TryDeleteDirectory(string path) {
        try {
            if (!Directory.Exists(path)) {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0) {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(path, recursive: true);
        } catch {
            // Best-effort cleanup: a leftover temp folder never fails a test whose assertions ran.
        }
    }
}
