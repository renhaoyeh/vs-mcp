using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace VsMcp.IntegrationTests;

/// <summary>
/// The tool catalog snapshot must match what the server declares. After an intended schema change,
/// regenerate tests/VsMcp.TestProjects/tools.json with VS_MCP_UPDATE_TOOL_CATALOG=1 and review the diff.
/// </summary>
[Collection("Fixture")]
public sealed class ToolReferenceTests(FixtureSession session)
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public async Task The_tool_catalog_snapshot_matches_the_declared_tools()
    {
        var tools = (await session.Client.ListToolsAsync()).OrderBy(tool => tool.Name, StringComparer.Ordinal).Select(tool =>
        {
            var protocol = tool.ProtocolTool;
            var entry = new JsonObject
            {
                ["name"] = protocol.Name,
                ["title"] = protocol.Title,
                ["description"] = protocol.Description,
                ["readOnly"] = protocol.Annotations?.ReadOnlyHint,
                ["destructive"] = protocol.Annotations?.DestructiveHint,
                ["idempotent"] = protocol.Annotations?.IdempotentHint,
                ["openWorld"] = protocol.Annotations?.OpenWorldHint,
                ["inputSchema"] = JsonNode.Parse(protocol.InputSchema.GetRawText()),
                ["outputSchema"] = protocol.OutputSchema is { } output ? JsonNode.Parse(output.GetRawText()) : null,
            };
            return (JsonNode)entry;
        }).ToArray();

        var actual = new JsonObject { ["tools"] = new JsonArray(tools) }.ToJsonString(Pretty).Replace("\r\n", "\n") + "\n";
        var path = Path.Combine(ExpHost.ProjectRoot, "tests", "VsMcp.TestProjects", "tools.json");

        if (Environment.GetEnvironmentVariable("VS_MCP_UPDATE_TOOL_CATALOG") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), "tests/VsMcp.TestProjects/tools.json is missing. Generate it with VS_MCP_UPDATE_TOOL_CATALOG=1.");
        Assert.True(File.ReadAllText(path).Replace("\r\n", "\n") == actual,
            "tests/VsMcp.TestProjects/tools.json is out of date. Regenerate it with VS_MCP_UPDATE_TOOL_CATALOG=1 and review the diff.");
    }
}
