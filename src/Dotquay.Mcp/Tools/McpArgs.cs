using System.Text.Json;

namespace Dotquay.Mcp.Tools;

/// <summary>
/// Reads a tool call's arguments out of the untyped bag the SDK hands the handler
/// (<c>CallToolRequestParams.Arguments</c>), tolerating whatever shape a client actually sent rather
/// than throwing on the first surprise.
/// </summary>
/// <remarks>
/// A client is not obligated to omit a null, send the exact JSON kind the schema implies, or leave
/// out fields it doesn't understand — and an agent composing arguments freeform is more likely than a
/// hand-written client to send <c>"explain": "true"</c> (a string) where the schema says boolean.
/// Every getter here treats a present-but-wrong-kind value the same as an absent one and falls back,
/// rather than failing the whole call over one malformed argument that the tool may not even need.
/// </remarks>
internal static class McpArgs
{
    public static string? GetString(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments is not null
        && arguments.TryGetValue(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool GetBool(IDictionary<string, JsonElement>? arguments, string name, bool defaultValue = false)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    public static IReadOnlyList<string> GetStringArray(IDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<string>();

        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text)
            {
                items.Add(text);
            }
        }

        return items;
    }
}
