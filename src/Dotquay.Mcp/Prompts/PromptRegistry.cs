using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Prompts;

/// <summary>
/// The catalogue of every prompt this server exposes, and the two handlers
/// <see cref="Dotquay.Mcp.McpServerBuilder"/> wires to <c>prompts/list</c> and <c>prompts/get</c>.
/// </summary>
/// <remarks>
/// A plain list, exactly like <see cref="Tools.ToolRegistry"/> — a linear scan over three prompts is
/// immaterial, and it means adding a fourth prompt is one line in
/// <see cref="Dotquay.Mcp.McpServerBuilder"/>, never two.
/// </remarks>
public sealed class PromptRegistry(IReadOnlyList<IMcpPrompt> prompts) {
    public ValueTask<ListPromptsResult> ListAsync(RequestContext<ListPromptsRequestParams> context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ListPromptsResult {
            Prompts = prompts
                .Select(prompt => new Prompt {
                    Name = prompt.Name,
                    Description = prompt.Description,
                    Arguments = prompt.Arguments.Count == 0 ? null : prompt.Arguments.ToList(),
                })
                .ToList(),
        });

    public ValueTask<GetPromptResult> GetAsync(RequestContext<GetPromptRequestParams> context, CancellationToken cancellationToken) {
        var name = context.Params?.Name;
        var prompt = prompts.FirstOrDefault(p => p.Name == name);

        if (prompt is null) {
            throw new McpException($"No such prompt: '{name}'. Call prompts/list for the prompts this server exposes.");
        }

        return prompt.GetAsync(context, cancellationToken);
    }
}
