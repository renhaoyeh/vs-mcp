using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VsMcp.Infrastructure;

namespace VsMcp.Bridge;

internal static class Program
{
    public static McpServerOptions CreateOptions(BridgeConnection connection, ToolCatalog catalog)
    {
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        var controls = new BridgeTools(connection);
        foreach (var method in typeof(BridgeTools).GetMethods().Where(method => Attribute.IsDefined(method, typeof(McpServerToolAttribute))))
            tools.Add(McpServerTool.Create(method, controls, new McpServerToolCreateOptions { SerializerOptions = BridgeConnection.Json }));
        foreach (var tool in catalog.Tools) tools.Add(new ForwardingTool(tool, connection));
        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "vs-mcp-bridge", Title = "Visual Studio MCP Bridge", Version = typeof(Program).Assembly.GetName().Version!.ToString(3) },
            ServerInstructions = "Use list_vs_instances, then connect_vs_instance with the chosen PID and instanceId before calling IDE tools. "
                + "Confirm the returned solution and process identity. The binding is local to this MCP connection. "
                + "A restarted VS requires selecting the new instance explicitly. Interrupted mutations are never retried; inspect their effects before issuing another request. "
                + "Prefer VS MCP for builds. If a project appears missing, confirm get_vs_info and get_projects(recursive: true); use project_reload for an unloaded project already in the solution. "
                + "It cannot add an absent project. Switching solutions or adding projects must stay within the user's authorized scope. "
                + "When get_projects reports isLoaded false, call project_reload with that uniqueName, confirm isLoaded true, then continue the requested VS MCP build. An unloaded project alone does not justify fallback. If the VS MCP path remains blocked after inspection, a command-line build may be used; explain the blocker and fallback reason, and preserve unrelated running applications.",
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
            ToolCollection = tools, ScopeRequests = false,
        };
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0)
        {
            await Console.Error.WriteLineAsync("Usage: VsMcp.Bridge (MCP over stdin/stdout; select the VS with connect_vs_instance)");
            return 1;
        }
        try
        {
            var catalog = ToolCatalog.Load();
            var options = CreateOptions(new BridgeConnection(new InstanceRegistry(), catalog), catalog);
            await using var server = McpServer.Create(new StdioServerTransport(options), options);
            await server.RunAsync();
            return 0;
        }
        catch (Exception error) { await Console.Error.WriteLineAsync(error.ToString()); return 1; }
    }
}
