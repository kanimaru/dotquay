using Xunit;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// Design doc §8 test layer 5: nothing an MCP session does writes to real stdout.
/// </summary>
/// <remarks>
/// This is the in-process half of that guarantee. It proves the Generic Host's
/// <c>builder.Logging.ClearProviders()</c> call (<see cref="Dotquay.Mcp.McpServerBuilder"/>)
/// actually takes effect and that no tool path reaches for <see cref="Console.WriteLine(string)"/>
/// directly — but <see cref="McpTestHarness"/> talks to the server over an in-memory pipe, never
/// real stdio, so it cannot by itself catch something that writes only when stdout is a real,
/// un-redirected console (there is no such code path today, but nothing here would fail if one were
/// added). Phase 1's exit criterion — a real NativeAOT publish driven end to end and its raw stdout
/// bytes inspected (docs/DESIGN-v0.18-MCP.md §9) — is what closes that last gap; this test is the
/// fast, always-on regression guard for everything short of it.
/// </remarks>
public sealed class StdoutPurityTests {
    [Fact]
    public async Task A_full_session_writes_nothing_to_real_stdout() {
        var projectDirectory = Directory.CreateTempSubdirectory("dotquay-mcp-stdout-test-")
            .FullName;

        try {
            await using var harness = await McpTestHarness.StartAsync([projectDirectory]);

            // Touches every handler category once: tools/list, a failing tools/call (no manifest),
            // and resources/list — each is a distinct path that could, in principle, reach for
            // Console.WriteLine instead of returning its result through the transport.
            await harness.Client.ListToolsAsync();
            await harness.Client.CallToolAsync(
                "dotquay_status",
                new Dictionary<string, object?> { ["project"] = projectDirectory });
            await harness.Client.ListResourcesAsync();

            Assert.Equal(string.Empty, harness.CapturedStdout);
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }
}
