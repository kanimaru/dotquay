using System.Diagnostics;
using System.Text.Json;
using Dotquay.Core.IO;
using Xunit;

namespace Dotquay.Tests.Cli;

/// <summary>
/// <c>--verbose</c> end to end, through the real apphost: the guarantee that matters is the one no
/// in-process test can show - that the diagnostic lines land on the process's real stderr and
/// <c>--json</c>'s stdout is still exactly one parseable document (SPEC.md section 17,
/// docs/DESIGN-v0.24-VERBOSE.md).
/// </summary>
/// <remarks>
/// <c>doctor</c> because it is the cheapest command that shells out (<c>git --version</c>) without
/// touching the network. Set <c>DOTQUAY_TEST_CLI_EXE</c> to run the same assertions against a
/// NativeAOT publish instead of the framework-dependent apphost.
/// </remarks>
public sealed class VerboseRealProcessTests {
    [Fact]
    public async Task Verbose_json_keeps_stdout_one_document_and_logs_git_to_stderr() {
        await WithProjectAsync(async (cli, project) => {
            var (exitCode, stdout, stderr) = await RunAsync(cli,
                ["doctor", "--json", "--verbose", "--project", project]);

            AssertSingleJsonDocument(stdout);
            Assert.Contains("| dotquay ", stderr);
            Assert.Contains($"| project: {project}", stderr);
            Assert.Contains("| $ git --version", stderr);
            Assert.Matches(@"git +\| exit 0 after \d+ ms", stderr);
            Assert.Matches($@"dotquay +\| exit {exitCode} after \d+ ms", stderr);
        });
    }

    [Fact]
    public async Task Environment_variable_turns_it_on_and_trace_adds_captured_output() {
        await WithProjectAsync(async (cli, project) => {
            var (_, stdout, stderr) = await RunAsync(
                cli, ["doctor", "--json", "--project", project], ("DOTQUAY_VERBOSE", "trace"));

            AssertSingleJsonDocument(stdout);
            Assert.Contains("| diagnostics: trace", stderr);
            Assert.Contains("stdout| git version", stderr);
        });
    }

    [Fact]
    public async Task Without_it_stderr_carries_no_diagnostics() {
        await WithProjectAsync(async (cli, project) => {
            var (_, stdout, stderr) = await RunAsync(cli,
                ["doctor", "--json", "--project", project], ("DOTQUAY_VERBOSE", ""));

            AssertSingleJsonDocument(stdout);
            Assert.DoesNotContain("$ git", stderr);
            Assert.DoesNotContain("| dotquay ", stderr);
        });
    }

    [Fact]
    public async Task Verbose_is_accepted_before_the_subcommand_and_as_short_v() {
        await WithProjectAsync(async (cli, project) => {
            var (exitCode, stdout, stderr) = await RunAsync(cli,
                ["-v", "list", "--json", "--project", project]);

            Assert.Equal(0, exitCode);
            AssertSingleJsonDocument(stdout);
            Assert.Contains("| args: -v list --json --project", stderr);
        });
    }

    private static void AssertSingleJsonDocument(string stdout) {
        using var document = JsonDocument.Parse(stdout); // throws on trailing content
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
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

        var project = Directory.CreateTempSubdirectory("dotquay-verbose-test-").FullName;
        var home = Directory.CreateTempSubdirectory("dotquay-verbose-home-").FullName;
        var cli = new CliUnderTest(exe, home);
        try {
            var (initExit, _, initErr) = await RunAsync(cli,
                ["init", "--json", "--project", project]);
            Assert.True(initExit == 0, initErr);
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        CliUnderTest cli, string[] args, params (string Name, string Value)[] environment) {
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

        // Isolated from whatever the developer running the suite has exported, and from the update
        // check, which would otherwise add network lines to stderr.
        startInfo.Environment.Remove("DOTQUAY_VERBOSE");
        startInfo.Environment["DOTQUAY_NO_UPDATE_CHECK"] = "1";
        startInfo.Environment[SystemEnvironmentReader.HomeOverrideVariable] = cli.Home;
        foreach (var (name, value) in environment) {
            startInfo.Environment[name] = value;
        }

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
