using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// Design doc Â§6: <c>--log &lt;file&gt;</c> appends a plain-text trace of every request, and
/// <c>logging/setLevel</c> is accepted so a client can ask for warning-and-above events.
/// </summary>
public sealed class LoggingTests
{
    [Fact]
    public async Task A_log_file_receives_one_line_per_request()
    {
        var projectDirectory = Directory.CreateTempSubdirectory("dotquay-mcp-logging-").FullName;
        var logFile = Path.Combine(Path.GetTempPath(), $"dotquay-mcp-log-{Guid.NewGuid():N}.log");

        try
        {
            var harness = await McpTestHarness.StartAsync([projectDirectory], logFile: logFile);

            try
            {
                await harness.Client.ListToolsAsync();
                await harness.Client.CallToolAsync(
                    "dotquay_status", new Dictionary<string, object?> { ["project"] = projectDirectory });
            }
            finally
            {
                // Disposing runs the host's ApplicationStopping hook, which closes the log file
                // (RequestLog.Dispose) — every write is already flushed immediately, but this makes
                // sure the file handle isn't still open when this test reads it back below.
                await harness.DisposeAsync();
            }

            var lines = File.ReadAllLines(logFile);
            Assert.Contains(lines, line => line.Contains("method=tools/list", StringComparison.Ordinal) && line.Contains(" ok", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("method=tools/call", StringComparison.Ordinal) && line.Contains(" ok", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
            File.Delete(logFile);
        }
    }

    [Fact]
    public async Task Logging_setLevel_is_accepted()
    {
        var projectDirectory = Directory.CreateTempSubdirectory("dotquay-mcp-logging-setlevel-").FullName;

        try
        {
            // Pinned to the same protocol revision the real-process tests and the Phase 0 spike use
            // (WriteToolsRealProcessTests.cs, verify-phase2-aot.ps1: "2025-06-18"), not the SDK
            // client's own newest-supported default: found while writing this test that under the
            // newer revision the installed SDK also understands (2026-07-28), the *client* itself
            // refuses to send `logging/setLevel` at all — "the method 'logging/setLevel' is not
            // available on protocol version '2026-07-28'. Use the per-request
            // '_meta/io.modelcontextprotocol/logLevel' field instead." That newer revision has
            // replaced the whole request/notification mechanism design doc Â§6 specifies with a
            // per-request metadata field. Nothing in this server needs to change for that — a client
            // that negotiates 2025-06-18 (today's real clients, including every one named in
            // docs/DESIGN-v0.18-MCP.md Â§1) still gets exactly what Â§6 describes — but a future spec
            // bump replacing Â§6's mechanism outright is now a known, real constraint, not a hypothetical.
            var clientOptions = new McpClientOptions { ProtocolVersion = "2025-06-18" };
            await using var harness = await McpTestHarness.StartAsync([projectDirectory], clientOptions);

            // Throws on failure (an unhandled request or a protocol error); the point of this test
            // is that a client asking for a level is accepted, not what happens after.
            await harness.Client.SetLoggingLevelAsync(LoggingLevel.Warning);
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }
}
