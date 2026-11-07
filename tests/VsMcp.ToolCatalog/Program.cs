using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;

// Produces the real SDK schemas without launching or automating Visual Studio.
internal static class Program
{
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int Run(string[] args)
    {
        var assembly = Assembly.Load("VsMcp");
        var type = assembly.GetType("VsMcp.Tools.VsToolProvider", true);
        var provider = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { null, null }, null);
        var tools = (IReadOnlyList<McpServerTool>)type.GetProperty("Tools").GetValue(provider);
        if (tools.Select(t => t.ProtocolTool.Name).Distinct().Count() != tools.Count) throw new Exception("Duplicate tool names.");
        foreach (var tool in tools)
        {
            var p = tool.ProtocolTool;
            if (string.IsNullOrWhiteSpace(p.Description) || p.Annotations == null) throw new Exception("Missing documentation or annotations: " + p.Name);
            if (p.InputSchema.GetProperty("properties").EnumerateObject().Any(a => a.Name.Contains("_"))) throw new Exception("Non-camelCase argument: " + p.Name);
        }
        var catalog = new { tools = tools.OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal).Select(t => {
            var p = t.ProtocolTool;
            return new { name = p.Name, title = p.Title, description = p.Description, readOnly = p.Annotations?.ReadOnlyHint, destructive = p.Annotations?.DestructiveHint, idempotent = p.Annotations?.IdempotentHint, openWorld = p.Annotations?.OpenWorldHint, inputSchema = p.InputSchema, outputSchema = p.OutputSchema };
        }).ToArray() };
        var json = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n";
        var destination = args.Length > 0 ? args[0] : "src/VsMcp.Bridge/tools.json";
        if (args.Length > 1 && args[1] == "--check")
        {
            if (File.ReadAllText(destination).Replace("\r\n", "\n") != json) throw new Exception("Tool catalog is out of date.");
        }
        else File.WriteAllText(destination, json, new System.Text.UTF8Encoding(false));
        Console.WriteLine("Validated " + tools.Count + " SDK tool definitions.");
        return 0;
    }
}
