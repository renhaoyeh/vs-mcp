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
            ServerInfo = new Implementation { Name = "vs-mcp-bridge", Title = "Visual Studio MCP Bridge", Version = "0.5.0" },
            ServerInstructions = "Use list_vs_instances, then connect_vs_instance with the chosen PID and instanceId before calling IDE tools. "
                + "Confirm the returned solution and process identity. The binding is local to this MCP connection. "
                + "A restarted VS requires selecting the new instance explicitly. Interrupted mutations are never retried; inspect their effects before issuing another request.",
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
            var lockPath = Path.Combine(AppContext.BaseDirectory, ".bridge.lock");
            using var deploymentLock = File.Exists(lockPath) ? File.Open(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite) : null;
            var catalog = ToolCatalog.Load();
            var options = CreateOptions(new BridgeConnection(new InstanceRegistry(), catalog), catalog);
            await using var server = McpServer.Create(new StdioServerTransport(options), options);
            await server.RunAsync();
            return 0;
        }
        catch (Exception error) { await Console.Error.WriteLineAsync(error.ToString()); return 1; }
    }
}
