using System.Diagnostics;
using System.Text.Json;
using Dotquay.Core.IO;
using Xunit;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// Phase 2's own exit criterion (docs/DESIGN-v0.18-MCP.md §9): "a real private-feed refusal/approval
/// loop and a real confirm_discard loop walked on the AOT binary." Test layer 6 (§8) describes this as
/// a manual/scripted verification step, but the same real-process pattern
/// <c>Dotquay.Tests.Git.ProcessGitClientRealProcessTests</c> used to catch Phase 1's git-stdin
/// deadlock is cheap enough, and valuable enough as regression protection, to keep as a permanent
/// test: it spawns the real apphost exe's <c>mcp</c> subcommand over real OS pipes and drives it with
/// raw JSON-RPC against a real local git repository (a <c>file://</c> remote — no network access
/// required), never the in-process test harness.
/// </summary>
/// <remarks>
/// The addon's package manifest declares a NuGet feed but no <c>[dotnet.*]</c> package that needs it,
/// so approving the feed never requires actually resolving anything from it — the point of this test
/// is the write guard's refuse/approve decision, not NuGet resolution.
/// </remarks>
public sealed class WriteToolsRealProcessTests {
    [Fact]
    public async Task Add_walks_a_real_feed_refusal_and_approval_loop_then_update_walks_a_real_confirm_discard_loop() {
        var cliExe = Environment.GetEnvironmentVariable("DOTQUAY_TEST_CLI_EXE")
            ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "dotquay.exe" : "dotquay");
        Assert.True(File.Exists(cliExe), $"Expected {cliExe} to exist (either DOTQUAY_TEST_CLI_EXE, or alongside the test binaries via the ProjectReference on Dotquay.Cli).");

        if (!IsGitAvailable()) {
            // This test needs a real git to create and tag a real local repository; every other MCP
            // test in this suite runs against fakes and does not need this skip.
            return;
        }

        const string FeedUrl = "https://example-private-feed.test/v3/index.json";
        var addonRepo = CreateAddonRepo(FeedUrl);
        var addonUrl = new Uri(addonRepo).AbsoluteUri;
        var projectDirectory = Directory.CreateTempSubdirectory("dotquay-mcp-write-test-").FullName;

        // The child's home, so the add's fetch lands in a temp cache rather than the developer's
        // real ~/.dotquay/cache (and no real credentials or config are read).
        var homeDirectory = Directory.CreateTempSubdirectory("dotquay-mcp-write-home-").FullName;

        try {
            await RunOneShotAsync(cliExe, homeDirectory, ["init", "--json", "--project", projectDirectory]);

            var startInfo = new ProcessStartInfo(cliExe) {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment[SystemEnvironmentReader.HomeOverrideVariable] = homeDirectory;
            startInfo.ArgumentList.Add("mcp");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(projectDirectory);

            using var server = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Process.Start returned null for '{cliExe}'.");

            // Drained, never just redirected: dotquay mcp writes its request trace - and under
            // DOTQUAY_VERBOSE its diagnostics (docs/DESIGN-v0.24-VERBOSE.md) - to stderr, and a
            // redirected stream nobody reads fills its pipe buffer and blocks the server mid-response.
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
                        clientInfo = new { name = "write-tools-regression-test", version = "0.0.1" },
                    },
                });
                await ReadResponseAsync(server.StandardOutput, 1);
                await SendAsync(server.StandardInput, new { jsonrpc = "2.0", method = "notifications/initialized" });

                // 1. Refused: the addon declares a feed this project has never agreed to trust.
                var refusal = await CallToolAsync(
                    server, id: 2, "dotquay_add",
                    new { project = projectDirectory, git_url = addonUrl, path = "addons/widget" });

                Assert.True(refusal.IsError);
                Assert.Equal("needs_confirmation", refusal.Content.GetProperty("error").GetProperty("kind").GetString());
                var refusalDetail = refusal.Content.GetProperty("detail");
                Assert.Equal("source_approval", refusalDetail.GetProperty("policy").GetString());
                var unapproved = Assert.Single(refusalDetail.GetProperty("unapproved_sources").EnumerateArray());
                Assert.Equal(FeedUrl, unapproved.GetProperty("url").GetString());

                // 2. Approved: the same call, with the refused feed named back.
                var added = await CallToolAsync(
                    server, id: 3, "dotquay_add",
                    new { project = projectDirectory, git_url = addonUrl, path = "addons/widget", approved_source_urls = new[] { FeedUrl } });

                Assert.False(added.IsError);
                var addonName = added.Content.GetProperty("data").GetProperty("changes")[0].GetProperty("name").GetString()!;
                var installedFolder = Path.Combine(projectDirectory, "addons", addonName);
                Assert.True(Directory.Exists(installedFolder), $"Expected {installedFolder} to exist after a successful add.");

                // 3. Hand-edit the installed addon directly on disk — a real, uncaptured modification.
                File.WriteAllText(Path.Combine(installedFolder, "extra-notes.txt"), "hours of work");

                // 4. force refused: the hand-edit is real content, not a uid/line-ending artifact.
                var forceRefusal = await CallToolAsync(
                    server, id: 4, "dotquay_update", new { project = projectDirectory, force = true });

                Assert.True(forceRefusal.IsError);
                Assert.Equal("needs_confirmation", forceRefusal.Content.GetProperty("error").GetProperty("kind").GetString());
                var forceDetail = forceRefusal.Content.GetProperty("detail");
                Assert.Equal("force_confirmation", forceDetail.GetProperty("policy").GetString());
                var unconfirmed = Assert.Single(forceDetail.GetProperty("unconfirmed_addons").EnumerateArray());
                Assert.Equal(addonName, unconfirmed.GetProperty("name").GetString());

                // 5. force + confirm_discard: the same call, naming the addon back.
                var forced = await CallToolAsync(
                    server, id: 5, "dotquay_update",
                    new { project = projectDirectory, force = true, confirm_discard = new[] { addonName } });

                Assert.False(forced.IsError);
            } finally {
                try {
                    server.Kill(entireProcessTree: true);
                } catch {
                    // Best-effort: the assertions above already reported anything worth reporting.
                }
            }
        } finally {
            // Best-effort cleanup: git leaves object files read-only under Windows, and
            // Directory.Delete(recursive: true) throws UnauthorizedAccessException on those before it
            // deletes anything else. Clearing ReadOnly first (and swallowing any remaining failure)
            // keeps a cleanup wrinkle from failing a test whose assertions already passed.
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(addonRepo);
            TryDeleteDirectory(homeDirectory);
        }
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
            // Best-effort: see the comment at the call site.
        }
    }

    private static string CreateAddonRepo(string feedUrl) {
        var dir = Directory.CreateTempSubdirectory("dotquay-addon-repo-").FullName;
        var addonPath = Path.Combine(dir, "addons", "widget");
        Directory.CreateDirectory(addonPath);

        File.WriteAllText(Path.Combine(addonPath, "plugin.cfg"), "[plugin]\nname=\"widget\"\n");
        File.WriteAllText(Path.Combine(addonPath, "dotquay.toml"), $"""
            [package]
            name = "widget"
            version = "1.0.0"

            [nuget.sources.private_feed]
            url = "{feedUrl}"
            packages = ["Some.Widget.*"]
            """);

        RunGit(dir, "init");
        RunGit(dir, "config", "user.email", "test@example.com");
        RunGit(dir, "config", "user.name", "Dotquay Tests");
        RunGit(dir, "add", "-A");
        RunGit(dir, "commit", "-m", "initial");
        RunGit(dir, "tag", "v1.0.0");

        return dir;
    }

    private static bool IsGitAvailable() {
        try {
            using var process = Process.Start(new ProcessStartInfo("git", "--version") {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process!.WaitForExit(5000);
            return process.ExitCode == 0;
        } catch {
            return false;
        }
    }

    private static void RunGit(string workingDirectory, params string[] args) {
        var startInfo = new ProcessStartInfo("git") {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null for 'git'.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"'git {string.Join(' ', args)}' in {workingDirectory} exited with code {process.ExitCode}: {process.StandardError.ReadToEnd()}");
    }

    private readonly record struct ToolCallResult(bool IsError, JsonElement Content);

    private static async Task<ToolCallResult> CallToolAsync(Process server, int id, string name, object arguments) {
        await SendAsync(server.StandardInput, new {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name, arguments },
        });

        var response = await ReadResponseAsync(server.StandardOutput, id);
        var result = response.RootElement.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var errorElement) && errorElement.GetBoolean();
        var structuredContent = result.GetProperty("structuredContent");

        return new ToolCallResult(isError, structuredContent);
    }

    private static async Task SendAsync(StreamWriter stdin, object message) {
        var json = JsonSerializer.Serialize(message);
        await stdin.WriteAsync(json + "\n");
        await stdin.FlushAsync();
    }

    /// <summary>Reads lines until one with the expected <c>id</c> arrives, tolerating notifications
    /// (which carry no <c>id</c>) interleaved before it.</summary>
    private static async Task<JsonDocument> ReadResponseAsync(StreamReader stdout, int expectedId) {
        using var overallTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        while (true) {
            string? line;
            try {
                line = await stdout.ReadLineAsync(overallTimeout.Token);
            } catch (OperationCanceledException) {
                Assert.Fail($"No response with id {expectedId} arrived on the MCP server's stdout within 30s.");
                throw; // Unreachable.
            }

            if (line is null) {
                Assert.Fail($"Server closed stdout before a response with id {expectedId} arrived.");
            }

            var document = JsonDocument.Parse(line);

            if (document.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
                && idElement.GetInt32() == expectedId) {
                return document;
            }
        }
    }

    private static async Task RunOneShotAsync(string exePath, string homeDirectory, string[] args) {
        var startInfo = new ProcessStartInfo(exePath) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment[SystemEnvironmentReader.HomeOverrideVariable] = homeDirectory;
        foreach (var arg in args) {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned null for '{exePath}'.");

        // Both streams drained concurrently, for the same full-pipe reason as the server above.
        var drainOutput = process.StandardOutput.ReadToEndAsync();
        var drainError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(drainOutput, drainError);
        Assert.True(process.ExitCode == 0, $"'{exePath} {string.Join(' ', args)}' exited with code {process.ExitCode}.");
    }
}
