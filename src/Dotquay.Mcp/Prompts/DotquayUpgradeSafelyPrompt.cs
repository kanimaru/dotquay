using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Prompts;

/// <summary>Upgrade every outdated addon one at a time, stopping at the first conflict marker
/// (design doc §7). Takes no arguments — it always operates on the whole project.</summary>
public sealed class DotquayUpgradeSafelyPrompt : IMcpPrompt {
    public string Name => "dotquay_upgrade_safely";

    public string Description =>
        "Upgrade every outdated addon one at a time: dotquay_outdated, dotquay_status --explain, "
        + "then dotquay_update per addon, stopping at the first conflict and pointing at "
        + "dotquay_patch_resolve.";

    public IReadOnlyList<PromptArgument> Arguments { get; } = [];

    public ValueTask<GetPromptResult> GetAsync(RequestContext<GetPromptRequestParams> context,
        CancellationToken cancellationToken) {
        var text = McpPromptText.Render("dotquay_upgrade_safely.md");

        return ValueTask.FromResult(new GetPromptResult {
            Description = Description,
            Messages = [new PromptMessage { Role = Role.User,
                Content = new TextContentBlock { Text = text } }],
        });
    }
}
