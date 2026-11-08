using System.Text;
using Dotquay.Core.Patch;
using Xunit;

namespace Dotquay.Tests.Patch;

[CollectionDefinition(nameof(ProcessPatchToolLineEndingTests), DisableParallelization = true)]
public sealed class ProcessPatchToolLineEndingCollection {
}

/// <summary>
/// Real-git tests that a patch captures and applies whatever line endings the trees use and
/// whatever <c>core.autocrlf</c> the machine has. The repo cache checks addons out through git, so
/// an upstream with <c>* text=auto</c> lands with CRLF on Windows, while a patch file holds LF:
/// with <c>core.autocrlf=false</c>, <c>git apply</c> then found no hunk matching and the install
/// failed. Each case forces the setting for the git processes it starts through
/// <c>GIT_CONFIG_COUNT</c>, never the user's own config, which is why the collection runs alone.
/// </summary>
[Collection(nameof(ProcessPatchToolLineEndingTests))]
public sealed class ProcessPatchToolLineEndingTests {
    private const string RelativeScript = "scripts/camera.gd";
    private const string UntouchedScript = "scripts/other.gd";
    private const string AddedScript = "scripts/added.gd";
    private const string Crlf = "\r\n";
    private const string Lf = "\n";

    private static readonly string[] _originalLines = ["func a():", "\tpass", "", "func b():",
        "\tpass"];
    private static readonly string[] _patchedLines = ["func a():", "\tpass", "",
        "func patched():", "\tpass", "", "func b():", "\tpass"];
    private static readonly string[] _untouchedLines = ["func c():", "\tpass"];
    private static readonly string[] _addedLines = ["func added():", "\tpass"];

    public static TheoryData<string, string, string> Cases() {
        var data = new TheoryData<string, string, string>();
        foreach (var autocrlf in new[] { "true", "input", "false" }) {
            foreach (var captureEol in new[] { Crlf, Lf }) {
                foreach (var targetEol in new[] { Crlf, Lf }) {
                    data.Add(autocrlf, Name(captureEol), Name(targetEol));
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CaptureAndApply_AnyLineEndings_PatchesInTheTargetsOwnStyle(
        string autocrlf, string captureEolName, string targetEolName) {
        var captureEol = Eol(captureEolName);
        var targetEol = Eol(targetEolName);
        var root = Directory.CreateTempSubdirectory("dotquay-patch-eol-").FullName;

        try {
            using var gitConfig = new GitConfigOverride("core.autocrlf", autocrlf);
            var tool = new ProcessPatchTool();

            var captureBase = Path.Combine(root, "capture-base");
            var captureOurs = Path.Combine(root, "capture-ours");
            WriteLines(captureBase, RelativeScript, _originalLines, captureEol);
            WriteLines(captureOurs, RelativeScript, _patchedLines, captureEol);
            WriteLines(captureOurs, AddedScript, _addedLines, captureEol);
            var patchText = await tool.DiffDirectoriesAsync(captureBase, captureOurs);

            var target = Path.Combine(root, "target");
            WriteLines(target, RelativeScript, _originalLines, targetEol);
            WriteLines(target, UntouchedScript, _untouchedLines, targetEol);
            var untouchedBefore = File.ReadAllBytes(Path.Combine(target, UntouchedScript));

            await tool.ApplyPatchAsync(patchText, target);

            Assert.Equal(Join(_patchedLines, targetEol),
                File.ReadAllText(Path.Combine(target, RelativeScript)));
            Assert.Equal(Join(_addedLines, targetEol),
                File.ReadAllText(Path.Combine(target, AddedScript)));
            Assert.Equal(untouchedBefore,
                File.ReadAllBytes(Path.Combine(target, UntouchedScript)));
        } finally {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("crlf")]
    [InlineData("lf")]
    public async Task ApplyPatchAsync_PatchWithCrlfLinesFromAnOlderCapture_Applies(
        string targetEolName) {
        // What capture produced before it normalised: CRLF trees diffed without core.autocrlf
        // put CR into every content line, while git's own header lines stay LF.
        var targetEol = Eol(targetEolName);
        var patchText = "diff --git a/base/scripts/camera.gd b/ours/scripts/camera.gd\n"
            + "--- a/base/scripts/camera.gd\n"
            + "+++ b/ours/scripts/camera.gd\n"
            + "@@ -2,3 +2,6 @@ func a():\n"
            + " \tpass\r\n"
            + " \r\n"
            + "+func patched():\r\n"
            + "+\tpass\r\n"
            + "+\r\n"
            + " func b():\r\n";
        var root = Directory.CreateTempSubdirectory("dotquay-patch-eol-").FullName;

        try {
            using var gitConfig = new GitConfigOverride("core.autocrlf", "false");
            var target = Path.Combine(root, "target");
            WriteLines(target, RelativeScript, _originalLines, targetEol);

            await new ProcessPatchTool().ApplyPatchAsync(patchText, target);

            Assert.Equal(Join(_patchedLines, targetEol),
                File.ReadAllText(Path.Combine(target, RelativeScript)));
        } finally {
            TryDeleteDirectory(root);
        }
    }

    private static string Name(string eol) => eol == Crlf ? "crlf" : "lf";

    private static string Eol(string name) => name == "crlf" ? Crlf : Lf;

    private static string Join(string[] lines, string eol) => string.Join(eol, lines) + eol;

    private static void WriteLines(string directory, string relativePath, string[] lines,
        string eol) {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Join(lines, eol)));
    }

    private static void TryDeleteDirectory(string path) {
        try {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        } catch {
            // Best-effort cleanup: a leftover temp folder never fails a test whose assertions ran.
        }
    }

    /// <summary>Sets one git config value for every git process this test process starts, through
    /// git's <c>GIT_CONFIG_COUNT</c> environment, and puts the previous environment back.</summary>
    private sealed class GitConfigOverride : IDisposable {
        private static readonly string[] _names = ["GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0",
            "GIT_CONFIG_VALUE_0"];

        private readonly string?[] _previous;

        public GitConfigOverride(string key, string value) {
            _previous = _names.Select(Environment.GetEnvironmentVariable).ToArray();
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", key);
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", value);
        }

        public void Dispose() {
            for (var i = 0; i < _names.Length; i++) {
                Environment.SetEnvironmentVariable(_names[i], _previous[i]);
            }
        }
    }
}
