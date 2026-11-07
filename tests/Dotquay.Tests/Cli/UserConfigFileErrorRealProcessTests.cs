using System.Diagnostics;
using System.Text.Json;
using Dotquay.Core.IO;
using Xunit;

namespace Dotquay.Tests.Cli;

/// <summary>
/// A malformed user-level file (<c>~/.dotquay/credentials.toml</c> or <c>config.toml</c>) through
/// the real apphost: <c>--json</c> prints one failure envelope of kind <c>invalid_config_file</c>,
/// the plain output is the parser's message rather than a stack trace, and an MCP tool reports the
/// same kind (SPEC.md section 17).
/// </summary>
/// <remarks>
/// <para>Before the kind existed, a broken credentials file was reported as <c>auth</c>, which made
/// the editor plugin suggest <c>dotquay login</c>, and login reads the same file and fails the same
/// way. A broken <c>config.toml</c> was <c>workflow</c>, and a plain <c>add</c> of a NuGet package
/// crashed with a stack trace.</para>
/// <para>Every child runs with <c>DOTQUAY_HOME</c> pointed at a temp folder, so the broken files
/// are never the developer's own. Set <c>DOTQUAY_TEST_CLI_EXE</c> to run the same assertions
/// against a NativeAOT publish.</para>
/// </remarks>
public sealed class UserConfigFileErrorRealProcessTests {
    private const string BrokenToml = "this is [ not toml\n";
    private const string CredentialsFile = "credentials.toml";
    private const string ConfigFile = "config.toml";

    /// <summary>Not contacted: the credential lookup reads the broken file before any
    /// request.</summary>
    private const string FeedUrl = "http://127.0.0.1:9/v3/index.json";

    public static TheoryData<string, string[], string> JsonCases => new()
    {
        { CredentialsFile, ["login", "--list", "--json"], "login" },
        { CredentialsFile, ["logout", "example.test", "--json"], "logout" },
        { CredentialsFile,
            ["add", "Some.Package", "--version", "1.0.0", "--source", "feed", "--json"], "add" },
        { ConfigFile, ["self", "check", "--json"], "self-check" },
    };

    [Theory]
    [MemberData(nameof(JsonCases))]
    public async Task Json_reports_a_malformed_user_file_as_invalid_config_file(
        string file, string[] command, string envelopeName) {
        await WithProjectAsync(async (cli, project) => {
            var brokenPath = WriteBroken(cli, file);

            var (exitCode, stdout, stderr) = await RunAsync(cli,
                [.. command, "--project", project]);

            Assert.Equal(1, exitCode);
            Assert.DoesNotContain("Unhandled exception", stderr);

            using var document = JsonDocument.Parse(stdout); // throws on empty or trailing content
            var root = document.RootElement;
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.Equal(envelopeName, root.GetProperty("command").GetString());

            var error = root.GetProperty("error");
            Assert.Equal("invalid_config_file", error.GetProperty("kind").GetString());

            var message = error.GetProperty("message").GetString();
            Assert.Contains(brokenPath, message);
            Assert.Contains("(1,6)", message);
        });
    }

    [Theory]
    [InlineData(CredentialsFile,
        new[] { "add", "Some.Package", "--version", "1.0.0", "--source", "feed" })]
    [InlineData(CredentialsFile, new[] { "login", "--list" })]
    [InlineData(ConfigFile, new[] { "self", "notify", "on" })]
    public async Task Plain_output_is_the_message_not_a_stack_trace(string file, string[] command) {
        await WithProjectAsync(async (cli, project) => {
            var brokenPath = WriteBroken(cli, file);

            var (exitCode, stdout, stderr) = await RunAsync(cli,
                [.. command, "--project", project]);
            var output = stdout + stderr;

            Assert.Equal(1, exitCode);
            Assert.Contains(brokenPath, output);
            Assert.Contains("(1,6)", output);
            Assert.DoesNotContain("Unhandled exception", output);
            Assert.DoesNotContain("Exception", output);
            Assert.DoesNotContain("   at ", output);
        });
    }

    [Fact]
    public async Task An_mcp_tool_reports_a_malformed_credentials_file_as_invalid_config_file() {
        await WithProjectAsync(async (cli, project) => {
            var brokenPath = WriteBroken(cli, CredentialsFile);

            using var server = Process.Start(StartInfo(cli, ["mcp", "--project", project]))
                ?? throw new InvalidOperationException("Process.Start returned null.");

            // Drained, never just redirected: the server traces every request to stderr, and a full
            // pipe would block it mid-response.
            server.ErrorDataReceived += (_, _) => { };
            server.BeginErrorReadLine();

            try {
                await SendAsync(server.StandardInput, new {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new {
                        protocolVersion = "2025-06-18",
                        capabilities = new { },
                        clientInfo = new { name = "user-config-file-test", version = "0.0.1" },
                    },
                });
                (await ReadResponseAsync(server.StandardOutput, 1)).Dispose();
                await SendAsync(server.StandardInput,
                    new { jsonrpc = "2.0", method = "notifications/initialized" });

                await SendAsync(server.StandardInput, new {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "tools/call",
                    @params = new {
                        name = "dotquay_add_dotnet",
                        arguments = new {
                            project,
                            package_id = "Some.Package",
                            version = "1.0.0",
                            nuget_source = "feed",
                        },
                    },
                });

                using var response = await ReadResponseAsync(server.StandardOutput, 2);
                var result = response.RootElement.GetProperty("result");
                Assert.True(result.GetProperty("isError").GetBoolean());

                var error = result.GetProperty("structuredContent").GetProperty("error");
                Assert.Equal("invalid_config_file", error.GetProperty("kind").GetString());
                Assert.Contains(brokenPath, error.GetProperty("message").GetString());
            } finally {
                server.StandardInput.Close();
                if (!server.WaitForExit(TimeSpan.FromSeconds(10))) {
                    server.Kill(entireProcessTree: true);
                }
            }
        });
    }

    /// <summary>The apphost and the home it runs under: a temp folder, so the child's
    /// <c>.dotquay</c> (cache, credentials, config) is never the developer's real one.</summary>
    private sealed record CliUnderTest(string Exe, string Home);

    private static string WriteBroken(CliUnderTest cli, string file) {
        var directory = Path.Combine(cli.Home, ".dotquay");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, file);
        File.WriteAllText(path, BrokenToml);
        return path;
    }

    private static async Task WithProjectAsync(Func<CliUnderTest, string, Task> body) {
        var exe =
            Environment.GetEnvironmentVariable("DOTQUAY_TEST_CLI_EXE") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "dotquay.exe"
                : "dotquay");

        Assert.True(File.Exists(exe),
            $"Expected {exe} to exist (Dotquay.Tests references Dotquay.Cli).");

        var project = Directory.CreateTempSubdirectory("dotquay-config-error-test-").FullName;
        var home = Directory.CreateTempSubdirectory("dotquay-config-error-home-").FullName;
        var cli = new CliUnderTest(exe, home);
        try {
            var (initExit, _, initErr) = await RunAsync(cli,
                ["init", "--json", "--project", project]);
            Assert.True(initExit == 0, initErr);

            // A feed the project declares itself, so adding a package from it needs no approval and
            // gets as far as looking up its credential. --no-verify: nothing is contacted.
            var (sourceExit, _, sourceErr) = await RunAsync(
                cli,
                ["source", "add", "feed", FeedUrl, "--no-verify", "--json", "--project", project]);
            Assert.True(sourceExit == 0, sourceErr);

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
        } catch (UnauthorizedAccessException) {
            // Best-effort cleanup.
        }
    }

    private static ProcessStartInfo StartInfo(CliUnderTest cli, string[] args) {
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
        return startInfo;
    }

    private static async Task<(int ExitCode, string Stdout,
        string Stderr)> RunAsync(CliUnderTest cli, string[] args) {
        using var process = Process.Start(StartInfo(cli, args))
            ?? throw new InvalidOperationException("Process.Start returned null.");
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdout, await stderr);
    }

    private static async Task SendAsync(StreamWriter stdin, object message) {
        await stdin.WriteAsync(JsonSerializer.Serialize(message) + "\n");
        await stdin.FlushAsync();
    }

    /// <summary>Reads lines until one with the expected <c>id</c> arrives, skipping
    /// notifications.</summary>
    private static async Task<JsonDocument> ReadResponseAsync(StreamReader stdout, int expectedId) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        while (true) {
            var line = await stdout.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException($"The server closed stdout before response "
                + $"{expectedId}.");

            var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number
                && id.GetInt32() == expectedId) {
                return document;
            }

            document.Dispose();
        }
    }
}
