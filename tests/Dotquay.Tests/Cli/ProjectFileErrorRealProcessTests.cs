using System.Diagnostics;
using System.Text.Json;
using Dotquay.Core.IO;
using Xunit;

namespace Dotquay.Tests.Cli;

/// <summary>
/// A malformed <c>dotquay.lock</c>, <c>dotquay.toml</c> or <c>dotquay.local.toml</c> through the
/// real apphost: <c>--json</c> must still print exactly one failure envelope of kind
/// <c>invalid_project_file</c> and exit 1, and the plain output must be the parser's message, not a
/// stack trace (SPEC.md section 17).
/// </summary>
/// <remarks>
/// Real process on purpose: the bug this guards against was an exception escaping every command's
/// own catch into System.CommandLine's default handler, which printed <c>Unhandled exception:</c>
/// and a stack trace on stderr and nothing on stdout. No in-process test of a command's code sees
/// that handler. Set <c>DOTQUAY_TEST_CLI_EXE</c> to run the same assertions against a NativeAOT
/// publish.
/// </remarks>
public sealed class ProjectFileErrorRealProcessTests {
    private const string BrokenToml = "this is [ not toml\n";

    public static TheoryData<string, string[]> JsonCases => new()
    {
        { "dotquay.lock", ["status", "--json"] },
        { "dotquay.lock", ["doctor", "--json"] },
        { "dotquay.lock", ["list", "--json"] },
        { "dotquay.lock", ["graph", "--json"] },
        { "dotquay.toml", ["doctor", "--json"] },
        { "dotquay.toml", ["install", "--json"] },
        { "dotquay.toml", ["outdated", "--json"] },
        { "dotquay.local.toml", ["doctor", "--json"] },
        { "dotquay.local.toml", ["install", "--json"] },
        { "dotquay.local.toml", ["update", "--json"] },
    };

    [Theory]
    [MemberData(nameof(JsonCases))]
    public async Task Json_reports_a_malformed_project_file_as_one_envelope(string file,
        string[] command) {
        await WithProjectAsync(async (cli, project) => {
            File.WriteAllText(Path.Combine(project, file), BrokenToml);

            var (exitCode, stdout, stderr) = await RunAsync(cli,
                [.. command, "--project", project]);

            Assert.Equal(1, exitCode);
            Assert.DoesNotContain("Unhandled exception", stderr);

            using var document = JsonDocument.Parse(stdout); // throws on empty or trailing content
            var root = document.RootElement;
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.Equal(command[0], root.GetProperty("command").GetString());

            var error = root.GetProperty("error");
            Assert.Equal("invalid_project_file", error.GetProperty("kind").GetString());

            // The path of the file and the parser's line:column, so the reader knows where to look.
            var message = error.GetProperty("message").GetString();
            Assert.Contains(Path.Combine(project, file), message);
            Assert.Contains("(1,6)", message);
        });
    }

    [Theory]
    [InlineData("dotquay.lock", "status")]
    [InlineData("dotquay.toml", "doctor")]
    public async Task Plain_output_is_the_message_not_a_stack_trace(string file, string command) {
        await WithProjectAsync(async (cli, project) => {
            File.WriteAllText(Path.Combine(project, file), BrokenToml);

            var (exitCode, stdout, stderr) = await RunAsync(cli, [command, "--project", project]);
            var output = stdout + stderr;

            Assert.Equal(1, exitCode);
            Assert.Contains(Path.Combine(project, file), output);
            Assert.Contains("(1,6)", output);
            Assert.DoesNotContain("Unhandled exception", output);
            Assert.DoesNotContain("Exception", output);
            Assert.DoesNotContain("   at ", output);
        });
    }

    /// <summary>The apphost and the home it runs under: a temp folder, so the child's
    /// <c>.dotquay</c> (cache, credentials, config) is never the developer's real one.</summary>
    private sealed record CliUnderTest(string Exe, string Home);

    private static async Task WithProjectAsync(Func<CliUnderTest, string, Task> body) {
        var exe =
            Environment.GetEnvironmentVariable("DOTQUAY_TEST_CLI_EXE") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "dotquay.exe"
                : "dotquay");

        Assert.True(File.Exists(exe),
            $"Expected {exe} to exist (Dotquay.Tests references Dotquay.Cli).");

        var project = Directory.CreateTempSubdirectory("dotquay-parse-error-test-").FullName;
        var home = Directory.CreateTempSubdirectory("dotquay-parse-error-home-").FullName;
        var cli = new CliUnderTest(exe, home);
        try {
            var (initExit, _, initErr) = await RunAsync(cli,
                ["init", "--json", "--project", project]);
            Assert.True(initExit == 0, initErr);

            // A lockfile too, so the commands that need one get as far as reading the broken file
            // instead of stopping at "no dotquay.lock".
            var (updateExit, _, updateErr) = await RunAsync(cli,
                ["update", "--json", "--project", project]);
            Assert.True(updateExit == 0, updateErr);

            await body(cli, project);
        } finally {
            TryDelete(project);
            TryDelete(home);
        }
    }

    private static void TryDelete(string directory) {
        try {
            Directory.Delete(directory, recursive: true);
        } catch (IOException) {
            // Best-effort cleanup.
        }
    }

    private static async Task<(int ExitCode, string Stdout,
        string Stderr)> RunAsync(CliUnderTest cli, string[] args) {
        var startInfo = new ProcessStartInfo(cli.Exe) {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args) {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment.Remove("DOTQUAY_VERBOSE");
        startInfo.Environment["DOTQUAY_NO_UPDATE_CHECK"] = "1";
        startInfo.Environment[SystemEnvironmentReader.HomeOverrideVariable] = cli.Home;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null.");
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdout, await stderr);
    }
}
