using System.Text.Json;
using Dotquay.Mcp.Scope;
using Dotquay.Mcp.Tools;
using Xunit;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// Design doc §8 test layer 3: every tool's advertised schema is well-formed and internally
/// consistent, independent of any running server.
/// </summary>
public sealed class ToolSchemaTests
{
    public static IEnumerable<object[]> Tools()
    {
        var scope = new ProjectScope([Directory.GetCurrentDirectory()]);
        var gate = new ProjectGate();

        IMcpTool[] tools =
        [
            new DotquayStatusTool(scope, gate),
            new DotquayListTool(scope, gate),
            new DotquayGraphTool(scope, gate),
            new DotquayOutdatedTool(scope, gate),
            new DotquayDoctorTool(scope, gate),
            new DotquayAddPreviewTool(scope, gate),
            new DotquaySourceListTool(scope, gate),
            new DotquayOverrideListTool(scope, gate),
        ];

        foreach (var tool in tools)
        {
            yield return [tool];
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Name_starts_with_dotquay_prefix(IMcpTool tool) =>
        Assert.StartsWith("dotquay_", tool.Name, StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(Tools))]
    public void Description_is_non_empty_and_names_read_only(IMcpTool tool)
    {
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));

        // Every Phase 1 tool is read-only (design doc §4.1); the description is the one place a
        // model deciding whether to call it learns that without reading the source.
        Assert.Contains("read-only", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void InputSchema_is_an_object_schema_with_a_project_property(IMcpTool tool)
    {
        var schema = tool.InputSchema;

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.Equal("object", schema.GetProperty("type").GetString());

        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("project", out var project));
        Assert.Equal("string", project.GetProperty("type").GetString());

        // additionalProperties: false everywhere, so a caller's typo in an argument name fails loudly
        // (as an unrecognized property) instead of being silently ignored.
        Assert.True(schema.TryGetProperty("additionalProperties", out var additional));
        Assert.Equal(JsonValueKind.False, additional.ValueKind);
    }

    [Fact]
    public void Every_tool_name_is_unique()
    {
        var names = Tools().Select(row => ((IMcpTool)row[0]).Name).ToList();
        Assert.Equal(names.Distinct(StringComparer.Ordinal).Count(), names.Count);
    }

    [Fact]
    public void Phase_1_exposes_exactly_the_eight_designed_read_tools()
    {
        var names = Tools().Select(row => ((IMcpTool)row[0]).Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "dotquay_status",
                "dotquay_list",
                "dotquay_graph",
                "dotquay_outdated",
                "dotquay_doctor",
                "dotquay_add_preview",
                "dotquay_source_list",
                "dotquay_override_list",
            },
            names);
    }
}
