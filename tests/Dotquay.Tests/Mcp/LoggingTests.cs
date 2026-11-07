using ModelContextProtocol.Client;
using Xunit;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// Design doc §6: <c>--log &lt;file&gt;</c> appends a plain-text trace of every request. The trace is
/// the server's only log output; the deprecated MCP Logging feature (<c>logging/setLevel</c>,
/// <c>notifications/message</c>) is not offered (SEP-2577).
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
}
