using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace VsMcp.Bridge;

internal sealed class ToolCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly JsonObject snapshot;
    public IReadOnlyList<Tool> Tools { get; }

    public ToolCatalog(JsonObject snapshot)
    {
        this.snapshot = snapshot;
        Tools = snapshot["tools"]!.AsArray().Select(node =>
        {
            var tool = node!;
            return new Tool
            {
                Name = tool["name"]!.GetValue<string>(),
                Title = tool["title"]?.GetValue<string>(),
                Description = tool["description"]?.GetValue<string>(),
                InputSchema = JsonSerializer.SerializeToElement(tool["inputSchema"], Json),
                OutputSchema = tool["outputSchema"] is { } output ? JsonSerializer.SerializeToElement(output, Json) : null,
                Annotations = new ToolAnnotations
                {
                    ReadOnlyHint = tool["readOnly"]?.GetValue<bool>(),
                    DestructiveHint = tool["destructive"]?.GetValue<bool>(),
                    IdempotentHint = tool["idempotent"]?.GetValue<bool>(),
                    OpenWorldHint = tool["openWorld"]?.GetValue<bool>(),
                },
            };
        }).ToArray();
    }

    public static ToolCatalog Load()
    {
        using var stream = typeof(ToolCatalog).Assembly.GetManifestResourceStream("VsMcp.Bridge.tools.json")!;
        return new ToolCatalog(JsonNode.Parse(stream)!.AsObject());
    }

    public void Validate(IEnumerable<Tool> tools)
    {
        var actual = new JsonObject { ["tools"] = new JsonArray(tools.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t =>
            JsonSerializer.SerializeToNode(new
            {
                name = t.Name, title = t.Title, description = t.Description,
                readOnly = t.Annotations?.ReadOnlyHint, destructive = t.Annotations?.DestructiveHint,
                idempotent = t.Annotations?.IdempotentHint, openWorld = t.Annotations?.OpenWorldHint,
                inputSchema = t.InputSchema, outputSchema = t.OutputSchema,
            }, Json)).ToArray()) };
        if (!JsonNode.DeepEquals(snapshot, actual))
        {
            var expectedTools = snapshot["tools"]!.AsArray();
            var actualTools = actual["tools"]!.AsArray();
            var difference = expectedTools.Count != actualTools.Count ? "tool count" : string.Join(", ", expectedTools.Zip(actualTools)
                .Where(pair => !JsonNode.DeepEquals(pair.First, pair.Second))
                .Select(pair => pair.First!["name"]!.GetValue<string>() + ": " + string.Join(", ", pair.First.AsObject()
                    .Where(property => !JsonNode.DeepEquals(property.Value, pair.Second![property.Key])).Select(property => property.Key))));
            throw new ModelContextProtocol.McpException("TOOL_CATALOG_MISMATCH: Install matching VSIX and bridge versions before connecting. Difference: " + difference);
        }
    }
}
