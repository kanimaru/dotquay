using Dotquay.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Prompts;

/// <summary>The plugin's add dialog as prose: preview, show the user what would change, get a yes,
/// then add for real (design doc §7).</summary>
public sealed class DotquayAddAddonPrompt : IMcpPrompt {
    public string Name => "dotquay_add_addon";

    public string Description =>
        "Add a git addon the careful way: dotquay_add_preview, show feeds/generators needing "
        + "approval, get the user's yes, then dotquay_add with approvals and a follow-up "
        + "dotquay_doctor.";

    public IReadOnlyList<PromptArgument> Arguments { get; } =
    [
        new PromptArgument
        {
            Name = "git_url",
            Description = "The addon's git repository URL to preview and add.",
            Required = true,
        },
    ];

    public ValueTask<GetPromptResult> GetAsync(RequestContext<GetPromptRequestParams> context,
        CancellationToken cancellationToken) {
        var gitUrl = McpArgs.GetString(context.Params?.Arguments, "git_url");

        if (string.IsNullOrWhiteSpace(gitUrl)) {
            throw new McpException("'git_url' is required: the git repository URL of the addon to "
                + "add.");
        }

        var text = McpPromptText.Render("dotquay_add_addon.md",
            new Dictionary<string, string> { ["git_url"] = gitUrl });

        return ValueTask.FromResult(new GetPromptResult {
            Description = Description,
            Messages = [new PromptMessage { Role = Role.User,
                Content = new TextContentBlock { Text = text } }],
        });
    }
}
