using Dotquay.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Prompts;

/// <summary>Diagnose why an addon shows <c>modified</c> and recommend exactly one fix
/// (design doc §7).</summary>
public sealed class DotquayDiagnoseModifiedPrompt : IMcpPrompt
{
    public string Name => "dotquay_diagnose_modified";

    public string Description =>
        "Diagnose why an addon shows modified in dotquay_status and recommend exactly one fix: capture "
        + "it as a patch, force-update with confirm_discard, or leave it alone.";

    public IReadOnlyList<PromptArgument> Arguments { get; } =
    [
        new PromptArgument
        {
            Name = "name",
            Description = "The addon name, as it appears in dotquay_status, to diagnose.",
            Required = true,
        },
    ];

    public ValueTask<GetPromptResult> GetAsync(RequestContext<GetPromptRequestParams> context, CancellationToken cancellationToken)
    {
        var name = McpArgs.GetString(context.Params?.Arguments, "name");

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new McpException("'name' is required: the addon name (as dotquay_status reports it) to diagnose.");
        }

        var text = McpPromptText.Render("dotquay_diagnose_modified.md", new Dictionary<string, string> { ["name"] = name });

        return ValueTask.FromResult(new GetPromptResult
        {
            Description = Description,
            Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }],
        });
    }
}
