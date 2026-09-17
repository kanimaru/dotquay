using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// Design doc §8 test layer 4: every <c>dotquay_*</c> name a prompt's rendered text mentions must
/// exist in the tool registry, so a renamed tool cannot silently orphan a prompt.
/// </summary>
public sealed partial class PromptIntegrityTests
{
    [GeneratedRegex("dotquay_[a-z_]+")]
    private static partial Regex ToolNamePattern();

    [Fact]
    public async Task Every_tool_name_a_prompt_mentions_exists_in_the_tool_registry()
    {
        var projectDirectory = Directory.CreateTempSubdirectory("dotquay-mcp-prompt-integrity-").FullName;

        try
        {
            await using var harness = await McpTestHarness.StartAsync([projectDirectory]);

            var toolNames = (await harness.Client.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var prompts = await harness.Client.ListPromptsAsync();

            Assert.NotEmpty(prompts);
            Assert.NotEmpty(toolNames);

            foreach (var prompt in prompts)
            {
                IReadOnlyDictionary<string, object?> arguments = prompt.Name switch
                {
                    "dotquay_diagnose_modified" => new Dictionary<string, object?> { ["name"] = "example-addon" },
                    "dotquay_add_addon" => new Dictionary<string, object?> { ["git_url"] = "https://example.com/addon.git" },
                    _ => new Dictionary<string, object?>(),
                };

                var result = await harness.Client.GetPromptAsync(prompt.Name, arguments);
                var text = string.Concat(result.Messages.Select(m => ((TextContentBlock)m.Content).Text));

                var mentioned = ToolNamePattern().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal);

                foreach (var name in mentioned)
                {
                    Assert.True(
                        toolNames.Contains(name),
                        $"Prompt '{prompt.Name}' mentions '{name}', which is not a registered tool name.");
                }
            }
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }
}
