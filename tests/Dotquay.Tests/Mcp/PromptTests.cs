using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// Design doc Â§7: the three canned prompt workflows list correctly, render their placeholders, and
/// validate their required arguments.
/// </summary>
public sealed class PromptTests : IAsyncDisposable
{
    private readonly string _projectDirectory = Directory.CreateTempSubdirectory("dotquay-mcp-prompt-test-").FullName;
    private McpTestHarness? _harness;

    private async Task<McpTestHarness> HarnessAsync() =>
        _harness ??= await McpTestHarness.StartAsync([_projectDirectory]);

    [Fact]
    public async Task Prompts_list_returns_exactly_the_three_designed_prompts()
    {
        var harness = await HarnessAsync();

        var prompts = await harness.Client.ListPromptsAsync();

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "dotquay_diagnose_modified",
                "dotquay_upgrade_safely",
                "dotquay_add_addon",
            },
            prompts.Select(p => p.Name).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Diagnose_modified_substitutes_the_addon_name_into_its_rendered_text()
    {
        var harness = await HarnessAsync();

        var result = await harness.Client.GetPromptAsync(
            "dotquay_diagnose_modified", new Dictionary<string, object?> { ["name"] = "Godot-MToon-Shader" });

        var text = ((TextContentBlock)Assert.Single(result.Messages).Content).Text;
        Assert.Contains("Godot-MToon-Shader", text, StringComparison.Ordinal);
        Assert.Contains("dotquay_status", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{{name}}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnose_modified_without_the_required_name_argument_fails_instead_of_rendering_a_blank_prompt()
    {
        var harness = await HarnessAsync();

        await Assert.ThrowsAnyAsync<McpException>(() =>
            harness.Client.GetPromptAsync("dotquay_diagnose_modified", new Dictionary<string, object?>()).AsTask());
    }

    [Fact]
    public async Task Add_addon_substitutes_the_git_url_into_its_rendered_text()
    {
        var harness = await HarnessAsync();

        var result = await harness.Client.GetPromptAsync(
            "dotquay_add_addon", new Dictionary<string, object?> { ["git_url"] = "https://example.com/addon.git" });

        var text = ((TextContentBlock)Assert.Single(result.Messages).Content).Text;
        Assert.Contains("https://example.com/addon.git", text, StringComparison.Ordinal);
        Assert.Contains("dotquay_add_preview", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_addon_without_the_required_git_url_argument_fails()
    {
        var harness = await HarnessAsync();

        await Assert.ThrowsAnyAsync<McpException>(() =>
            harness.Client.GetPromptAsync("dotquay_add_addon", new Dictionary<string, object?>()).AsTask());
    }

    [Fact]
    public async Task Upgrade_safely_takes_no_arguments_and_renders_its_static_text()
    {
        var harness = await HarnessAsync();

        var result = await harness.Client.GetPromptAsync("dotquay_upgrade_safely", new Dictionary<string, object?>());

        var text = ((TextContentBlock)Assert.Single(result.Messages).Content).Text;
        Assert.Contains("dotquay_outdated", text, StringComparison.Ordinal);
        Assert.Contains("dotquay_patch_resolve", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Getting_an_unknown_prompt_fails_cleanly()
    {
        var harness = await HarnessAsync();

        await Assert.ThrowsAnyAsync<McpException>(() =>
            harness.Client.GetPromptAsync("dotquay_does_not_exist", new Dictionary<string, object?>()).AsTask());
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
