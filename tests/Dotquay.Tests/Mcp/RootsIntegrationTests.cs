using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// Design doc §4.3: a client that advertises the <c>roots</c> capability gets its roots folded into
/// this server's <see cref="Dotquay.Mcp.Scope.IProjectScope"/>, live, the same way <c>--project</c>
/// roots are — and a client that never advertises the capability sees exactly the old behaviour.
/// </summary>
public sealed class RootsIntegrationTests
{
    [Fact]
    public async Task A_project_only_reachable_via_a_client_supplied_root_is_accepted_after_the_first_tool_call()
    {
        var serverRoot = Directory.CreateTempSubdirectory("dotquay-mcp-roots-server-").FullName;
        var clientRoot = Directory.CreateTempSubdirectory("dotquay-mcp-roots-client-").FullName;
        File.WriteAllText(Path.Combine(clientRoot, "dotquay.toml"), "[project]\n");

        var clientOptions = new McpClientOptions
        {
            Capabilities = new ClientCapabilities { Roots = new RootsCapability() },
            Handlers = new McpClientHandlers
            {
                RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult
                {
                    Roots = [new Root { Uri = new Uri(clientRoot).AbsoluteUri }],
                }),
            },
        };

        try
        {
            await using var harness = await McpTestHarness.StartAsync([serverRoot], clientOptions);

            // First tool call of the session: this is what triggers ClientRootsSync (design doc
            // §4.3's "on first call" integration point — see that type's own remarks for why).
            var status = await harness.Client.CallToolAsync(
                "dotquay_status", new Dictionary<string, object?> { ["project"] = clientRoot });

            Assert.False(status.IsError);

            // resources/list reads IProjectScope.Roots live (not a constructor-time snapshot), so it
            // should reflect the newly-added root now that the sync above has happened.
            var resources = await harness.Client.ListResourcesAsync();
            Assert.Contains(resources, r => r.Description is { } description && description.Contains(clientRoot, StringComparison.Ordinal));

            // A path that is neither the server's own root nor anything the client ever offered is
            // still refused — client roots widen the scope, they never replace or bypass it.
            var outsideDirectory = Directory.CreateTempSubdirectory("dotquay-mcp-roots-outside-").FullName;
            try
            {
                var outside = await harness.Client.CallToolAsync(
                    "dotquay_status", new Dictionary<string, object?> { ["project"] = outsideDirectory });

                Assert.True(outside.IsError);
                AssertErrorKind(outside, "scope");
            }
            finally
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(serverRoot, recursive: true);
            Directory.Delete(clientRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_client_that_never_advertises_roots_gets_no_roots_request_and_default_scope_still_applies()
    {
        var serverRoot = Directory.CreateTempSubdirectory("dotquay-mcp-roots-none-").FullName;
        var outsideDirectory = Directory.CreateTempSubdirectory("dotquay-mcp-roots-none-outside-").FullName;

        try
        {
            // Default client options (null) — exactly what every pre-Phase-3 test already used, and
            // ClientRootsSync.EnsureSyncedAsync must be a complete no-op for it (no roots/list ever
            // sent, since server.ClientCapabilities.Roots is null for a client that never asked).
            await using var harness = await McpTestHarness.StartAsync([serverRoot]);

            var result = await harness.Client.CallToolAsync(
                "dotquay_status", new Dictionary<string, object?> { ["project"] = outsideDirectory });

            Assert.True(result.IsError);
            AssertErrorKind(result, "scope");
        }
        finally
        {
            Directory.Delete(serverRoot, recursive: true);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    private static void AssertErrorKind(CallToolResult result, string expectedKind)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var document = JsonDocument.Parse(text);
        Assert.Equal(expectedKind, document.RootElement.GetProperty("error").GetProperty("kind").GetString());
    }
}
