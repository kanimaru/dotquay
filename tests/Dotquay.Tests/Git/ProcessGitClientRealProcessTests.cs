using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Dotquay.Tests.Git;

/// <summary>
/// Regression test for a real bug found while verifying <c>dotquay mcp</c>'s published NativeAOT
/// binary end to end (docs/DESIGN-v0.18-MCP.md Phase 1 exit criterion): <see cref="Dotquay.Core.Git.ProcessGitClient.RunGitAsync"/>
/// redirected git's stdout/stderr but not its stdin, so the git child inherited whatever handle
/// <em>dotquay's own</em> stdin happened to be. Under a plain CLI invocation that handle is a real
/// console and nothing goes wrong. Under <c>dotquay mcp</c> it's the anonymous pipe the MCP host's
/// stdio transport has a pending async read on to receive the next JSON-RPC message -- and spawning
/// git without giving it its own stdin, while that read is outstanding, deadlocks the git child before
/// it can even print `--version`. Every <c>doctor</c>/<c>status</c>/<c>graph</c> call that shells out
/// to git then hangs forever: no error, no response, nothing sent back to the MCP client.
/// </summary>
/// <remarks>
/// A fakes-based unit test structurally cannot see this, and neither can the in-process MCP test
/// harness (<c>Dotquay.Tests.Mcp.McpTestHarness</c>) -- it swaps the transport's logical streams for
/// an in-memory pipe, but the test host process itself still owns a real console, so the OS-level
/// stdin contention this bug depends on never occurs. Nor is it enough to run <c>dotquay doctor</c>
/// (the plain CLI command) with piped stdio (verified: that exits fine, fix or no fix) -- the hang
/// specifically needs the `mcp` subcommand's long-lived stdio transport actively holding stdin open
/// for the next request while a tool call is in flight. So this test drives the real thing: it spawns
/// `dotquay mcp` itself, over real OS pipes, and talks newline-delimited JSON-RPC to it directly,
/// the same way an MCP client (or the Python driver used to first find this bug) would.
/// </remarks>
public sealed class ProcessGitClientRealProcessTests
{
    [Fact]
    public async Task Doctor_tool_call_completes_quickly_over_the_real_mcp_stdio_transport()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The hang this guards against is a Windows console/pipe-handle-inheritance quirk;
            // git's process model under Unix shells doesn't reproduce it the same way.
            return;
        }

        // The apphost exe, not `dotnet dotquay.dll`: dotquay.exe is what `dotquay mcp` actually
        // publishes and runs as. Built and copied alongside the test binaries because Dotquay.Tests
        // references Dotquay.Cli as a ProjectReference.
        var cliExe = Path.Combine(AppContext.BaseDirectory, "dotquay.exe");
        Assert.True(File.Exists(cliExe), $"Expected {cliExe} to exist alongside the test binaries (Dotquay.Tests references Dotquay.Cli).");

        var projectDirectory = Directory.CreateTempSubdirectory("dotquay-git-realprocess-test-").FullName;

        try
        {
            await RunOneShotAsync(cliExe, ["init", "--json", "--project", projectDirectory]);

            var startInfo = new ProcessStartInfo(cliExe)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
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

            try
            {
                await SendAsync(server.StandardInput, new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { },
                        clientInfo = new { name = "process-git-client-regression-test", version = "0.0.1" },
                    },
                });

                var initializeResponse = await ReadLineWithTimeoutAsync(server.StandardOutput, TimeSpan.FromSeconds(10));
                Assert.Contains("\"serverInfo\"", initializeResponse);

                await SendAsync(server.StandardInput, new { jsonrpc = "2.0", method = "notifications/initialized" });

                await SendAsync(server.StandardInput, new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "tools/call",
                    @params = new { name = "dotquay_doctor", arguments = new { project = projectDirectory } },
                });

                // The actual regression target: `doctor` calls ProcessGitClient.IsAvailableAsync as
                // its very first step, which shells out to `git --version`. Before the fix, that
                // shell-out hung forever here -- this line never arrived, no matter how long we waited.
                var doctorResponse = await ReadLineWithTimeoutAsync(server.StandardOutput, TimeSpan.FromSeconds(15));

                Assert.Contains("\"id\":2", doctorResponse);
                Assert.Contains("healthy", doctorResponse);
            }
            finally
            {
                try
                {
                    server.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort: the assertions above already reported anything worth reporting.
                }
            }
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    private static async Task SendAsync(StreamWriter stdin, object message)
    {
        var json = JsonSerializer.Serialize(message);
        await stdin.WriteAsync(json + "\n");
        await stdin.FlushAsync();
    }

    private static async Task<string> ReadLineWithTimeoutAsync(StreamReader stdout, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            var line = await stdout.ReadLineAsync(cancellation.Token);
            return line ?? throw new InvalidOperationException("Server closed stdout before sending a response.");
        }
        catch (OperationCanceledException)
        {
            Assert.Fail(
                $"No line received on the MCP server's stdout within {timeout.TotalSeconds}s -- this is the exact "
                + "hang ProcessGitClient produced under `dotquay mcp` before redirecting its git children's "
                + "standard input.");

            throw; // Unreachable: Assert.Fail always throws.
        }
    }

    private static async Task RunOneShotAsync(string exePath, string[] args)
    {
        var startInfo = new ProcessStartInfo(exePath)
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned null for '{exePath}'.");

        // Both streams drained concurrently, for the same full-pipe reason as the server above.
        var drainOutput = process.StandardOutput.ReadToEndAsync();
        var drainError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(drainOutput, drainError);
        var exitCode = process.ExitCode;
        Assert.True(exitCode == 0, $"'{exePath} {string.Join(' ', args)}' exited with code {exitCode}.");
    }
}
