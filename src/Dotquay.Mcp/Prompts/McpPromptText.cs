using System.Reflection;

namespace Dotquay.Mcp.Prompts;

/// <summary>
/// Loads a prompt's markdown body from the embedded resources under <c>Prompts/*.md</c> (design doc
/// §7: "Prompt text lives in Dotquay.Mcp/Prompts/*.md as embedded resources, so it can be read and
/// edited as prose") and fills in its <c>{{placeholder}}</c> tokens.
/// </summary>
/// <remarks>
/// Embedded resources, not <c>string</c> literals inlined into each prompt class: the point of §7's
/// requirement is that the prose an agent actually sees can be read and edited as prose — a
/// <c>.md</c> file a contributor opens directly — rather than hunting for it inside C# string
/// concatenation. This is plain data loading (<see cref="Assembly.GetManifestResourceStream"/>), not
/// reflection-based discovery, so it carries none of the AOT risk §3.1 rejected attribute-based tool
/// discovery for.
/// </remarks>
internal static class McpPromptText {
    public static string Render(string resourceFileName, IReadOnlyDictionary<string, string>? placeholders = null) {
        var resourceName = $"Dotquay.Mcp.Prompts.{resourceFileName}";
        var assembly = typeof(McpPromptText).Assembly;

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Missing embedded prompt resource '{resourceName}'. Check the EmbeddedResource glob "
                + "in Dotquay.Mcp.csproj and that the file name matches exactly (case-sensitive).");

        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        if (placeholders is null) {
            return text;
        }

        foreach (var (key, value) in placeholders) {
            text = text.Replace("{{" + key + "}}", value);
        }

        return text;
    }
}
