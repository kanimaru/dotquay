using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// Design doc §8 test layer 1: the handlers <see cref="Dotquay.Mcp.McpServerBuilder"/> wires up
/// behave correctly end to end — initialize, <c>tools/list</c>, <c>tools/call</c> — over
/// <see cref="McpTestHarness"/>'s in-memory stream transport.
/// </summary>
public sealed class HandlerConformanceTests : IAsyncDisposable
{
    private readonly string _projectDirectory = Directory.CreateTempSubdirectory("dotquay-mcp-test-").FullName;
    private McpTestHarness? _harness;

    private async Task<McpTestHarness> HarnessAsync() =>
        _harness ??= await McpTestHarness.StartAsync([_projectDirectory]);

    [Fact]
    public async Task Tools_list_returns_exactly_the_eight_read_tools_and_eleven_write_tools()
    {
        var harness = await HarnessAsync();

        var tools = await harness.Client.ListToolsAsync();

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                // Reads (Phase 1).
                "dotquay_status",
                "dotquay_list",
                "dotquay_graph",
                "dotquay_outdated",
                "dotquay_doctor",
                "dotquay_add_preview",
                "dotquay_source_list",
                "dotquay_override_list",

                // Guarded writes (Phase 2).
                "dotquay_init",
                "dotquay_add",
                "dotquay_add_dotnet",
                "dotquay_install",
                "dotquay_update",
                "dotquay_remove",
                "dotquay_patch_capture",
                "dotquay_patch_resolve",
                "dotquay_patch_abandon",
                "dotquay_override_set",
                "dotquay_override_clear",
            },
            tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Calling_an_unknown_tool_fails_cleanly_instead_of_throwing()
    {
        var harness = await HarnessAsync();

        var result = await harness.Client.CallToolAsync("dotquay_does_not_exist", new Dictionary<string, object?>());

        Assert.True(result.IsError);
        AssertErrorKind(result, "unexpected");
    }

    [Fact]
    public async Task Add_preview_on_a_project_with_no_manifest_reports_no_manifest_not_a_crash()
    {
        // dotquay_status tolerates a missing manifest (it only reads the lockfile, defaulting to
        // empty — DependencyWorkflow.Status), so it can't exercise this path. dotquay_add_preview
        // calls ReadManifestOrThrow first (DependencyWorkflow.Preview.cs), guaranteeing it.
        var harness = await HarnessAsync();

        var result = await harness.Client.CallToolAsync(
            "dotquay_add_preview",
            new Dictionary<string, object?> { ["project"] = _projectDirectory, ["git"] = "https://example.com/addon.git" });

        Assert.True(result.IsError);
        AssertErrorKind(result, "no_manifest");
    }

    [Fact]
    public async Task Add_on_a_project_with_no_manifest_reports_no_manifest_not_a_crash()
    {
        // dotquay_add's write guard runs PreviewAddAsync first (SourceApprovalPolicy), which hits the
        // same ReadManifestOrThrow as dotquay_add_preview — so the missing manifest is caught before
        // AddAsync itself ever runs, and before anything is written.
        var harness = await HarnessAsync();

        var result = await harness.Client.CallToolAsync(
            "dotquay_add",
            new Dictionary<string, object?> { ["project"] = _projectDirectory, ["git_url"] = "https://example.com/addon.git" });

        Assert.True(result.IsError);
        AssertErrorKind(result, "no_manifest");
    }

    [Fact]
    public async Task Add_with_a_nuget_source_argument_is_refused_before_touching_the_manifest()
    {
        var harness = await HarnessAsync();

        var result = await harness.Client.CallToolAsync(
            "dotquay_add",
            new Dictionary<string, object?>
            {
                ["project"] = _projectDirectory,
                ["git_url"] = "https://example.com/addon.git",
                ["nuget_source"] = "some-feed",
            });

        Assert.True(result.IsError);
        AssertErrorKind(result, "workflow");
    }

    [Fact]
    public async Task Init_on_an_empty_directory_creates_a_project_manifest()
    {
        var harness = await HarnessAsync();

        var result = await harness.Client.CallToolAsync(
            "dotquay_init", new Dictionary<string, object?> { ["project"] = _projectDirectory });

        Assert.False(result.IsError);
        Assert.True(File.Exists(Path.Combine(_projectDirectory, "dotquay.toml")));
    }

    [Fact]
    public async Task A_project_path_outside_every_configured_root_is_refused_by_scope_not_read_from_disk()
    {
        var harness = await HarnessAsync();
        var outsideDirectory = Directory.CreateTempSubdirectory("dotquay-mcp-outside-").FullName;

        try
        {
            var result = await harness.Client.CallToolAsync(
                "dotquay_status", new Dictionary<string, object?> { ["project"] = outsideDirectory });

            Assert.True(result.IsError);
            AssertErrorKind(result, "scope");
        }
        finally
        {
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_write_tool_is_also_refused_by_scope_before_it_can_write_anything()
    {
        var harness = await HarnessAsync();
        var outsideDirectory = Directory.CreateTempSubdirectory("dotquay-mcp-outside-").FullName;

        try
        {
            var result = await harness.Client.CallToolAsync(
                "dotquay_init", new Dictionary<string, object?> { ["project"] = outsideDirectory });

            Assert.True(result.IsError);
            AssertErrorKind(result, "scope");
            Assert.False(File.Exists(Path.Combine(outsideDirectory, "dotquay.toml")));
        }
        finally
        {
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    private static void AssertErrorKind(CallToolResult result, string expectedKind)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var document = JsonDocument.Parse(text);

        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedKind, document.RootElement.GetProperty("error").GetProperty("kind").GetString());

        // StructuredContent must always be the same document as the text block — a client that reads
        // one must never see something different from a client that reads the other.
        Assert.True(result.StructuredContent.HasValue);
        Assert.Equal(text, result.StructuredContent.Value.GetRawText());
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }

        Directory.Delete(_projectDirectory, recursive: true);
    }
}
