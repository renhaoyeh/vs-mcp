using System.Text.Json;
using ModelContextProtocol;
using Xunit;

namespace VsMcp.IntegrationTests;

/// <summary>What the server declares: the tool list, annotations and schemas. Works with any solution open.</summary>
[Collection("Fixture")]
public sealed class ServerTests(FixtureSession session)
{
    private static readonly string[] ToolNames =
    {
        "debugger_attach", "editor_selection_get", "project_items", "test_discover",
        "get_build_settings", "set_build_settings", "debugger_open_dump", "debugger_save_dump",
        "debugger_list_modules", "debugger_get_exception", "debugger_get_symbol_settings", "debugger_set_symbol_settings",
        "breakpoint_enable", "build_configuration_set", "build_projects", "cancel_build", "debugger_add_breakpoint", "debugger_break", "debugger_continue", "debugger_evaluate", "debugger_get_callstack", "debugger_get_locals", "debugger_list_breakpoints", "debugger_list_threads", "debugger_remove_breakpoint", "debugger_select_frame", "debugger_select_thread", "debugger_step_into", "debugger_step_out", "debugger_step_over", "document_active", "document_close", "document_list", "document_open", "document_read", "document_save", "document_write", "editor_replace", "errors_list", "execute_code", "find_references", "get_build_status", "get_debug_status", "get_project_dependencies", "get_projects", "get_solution_configurations", "get_vs_info", "goto_definition", "output_list_panes", "output_read", "project_reload", "project_unload", "show_vs_window", "solution_close", "solution_open", "start_debugging", "start_without_debugging", "startup_project_get", "startup_project_set", "stop_debugging", "test_cancel", "test_run", "test_status", "without_debugging_reconcile",
    };

    [Fact]
    public async Task Shared_dependencies_use_the_running_hosts_binding_policy()
    {
        var result = await session.Client.Call<JsonElement>("execute_code", new { code =
            "var names = new[] { \"System.Text.Json\", \"Microsoft.Bcl.AsyncInterfaces\", \"System.Diagnostics.DiagnosticSource\", \"System.IO.Pipelines\", \"Microsoft.Extensions.Logging.Abstractions\", \"System.Text.Encodings.Web\", \"System.Collections.Immutable\", \"System.Threading.Channels\", \"Microsoft.Extensions.DependencyInjection.Abstractions\" }; " +
            "return System.AppDomain.CurrentDomain.GetAssemblies().Where(a => names.Contains(a.GetName().Name)).Select(a => { " +
            "var baseline = a.GetName(); baseline.Version = new System.Version(10, 0, 0, 0); " +
            "return new { name = a.GetName().Name, identity = a.FullName, policy = System.AppDomain.CurrentDomain.ApplyPolicy(baseline.FullName) }; }).ToArray();" });
        var assemblies = result.GetProperty("result").EnumerateArray().ToArray();
        Assert.Contains(assemblies, a => a.GetProperty("name").GetString() == "System.Text.Json");
        Assert.All(assemblies, a => Assert.Equal(a.GetProperty("policy").GetString(), a.GetProperty("identity").GetString()));
    }

    [Fact]
    public async Task Lists_every_tool_with_annotations()
    {
        var client = session.Client;
        var tools = await client.ListToolsAsync();

        Assert.Equal(ToolNames.OrderBy(n => n), tools.Select(t => t.Name).OrderBy(n => n));
        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), tool.Name + " has no description");
            Assert.NotNull(tool.ProtocolTool.Annotations);
        }

        var readOnly = tools.Where(t => t.ProtocolTool.Annotations!.ReadOnlyHint == true).Select(t => t.Name);
        Assert.Equal(new[] { "debugger_get_callstack", "debugger_get_locals", "debugger_list_breakpoints", "debugger_list_threads", "document_active", "document_list", "document_read", "errors_list", "find_references", "get_build_status", "get_debug_status", "get_project_dependencies", "get_projects", "get_solution_configurations", "get_vs_info", "goto_definition", "output_list_panes", "output_read", "startup_project_get", "test_status" }.Concat(new[] { "editor_selection_get", "project_items", "test_discover", "get_build_settings", "debugger_list_modules", "debugger_get_exception", "debugger_get_symbol_settings" }).OrderBy(n => n),
            readOnly.OrderBy(n => n));
    }

    [Fact]
    public async Task Schemas_use_camel_case_names()
    {
        var client = session.Client;
        foreach (var tool in await client.ListToolsAsync())
        {
            foreach (var schema in new[] { tool.JsonSchema, tool.ReturnJsonSchema ?? default })
            {
                if (schema.ValueKind != JsonValueKind.Object) continue;
                var names = PropertyNames(schema).ToList();
                Assert.DoesNotContain(names, name => name.Contains('_'));
            }
        }
    }

    [Fact]
    public async Task Build_input_has_no_configuration_and_bounds_the_timeout()
    {
        var client = session.Client;
        var build = (await client.ListToolsAsync()).Single(t => t.Name == "build_projects");
        var schema = build.JsonSchema;

        // The active configuration is always used, so there is nothing to choose or require.
        Assert.False(schema.TryGetProperty("required", out _));
        Assert.False(schema.GetProperty("properties").TryGetProperty("solutionConfiguration", out _));
        Assert.False(schema.GetProperty("properties").TryGetProperty("platform", out _));
        var timeout = schema.GetProperty("properties").GetProperty("timeoutSeconds");
        Assert.Equal(1, timeout.GetProperty("minimum").GetInt32());
        Assert.Equal(3600, timeout.GetProperty("maximum").GetInt32());
        Assert.Equal(600, timeout.GetProperty("default").GetInt32());
        Assert.True(schema.GetProperty("properties").GetProperty("stopOnFirstError").GetProperty("default").GetBoolean());
    }

    [Fact]
    public async Task Structured_tools_publish_an_output_schema()
    {
        var client = session.Client;
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name);
        foreach (var name in new[] { "get_vs_info", "get_projects", "get_solution_configurations", "get_project_dependencies", "build_projects", "get_build_status", "cancel_build" })
        {
            Assert.True(tools[name].ReturnJsonSchema.HasValue, name + " has no output schema");
        }
    }

    [Fact]
    public async Task An_unknown_tool_is_a_protocol_error()
    {
        var client = session.Client;
        await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("no_such_tool").AsTask());
    }

    private static IEnumerable<string> PropertyNames(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in schema.EnumerateObject())
            {
                if (member.Name == "properties" && member.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in member.Value.EnumerateObject())
                    {
                        yield return property.Name;
                        foreach (var inner in PropertyNames(property.Value)) yield return inner;
                    }
                }
                else
                {
                    foreach (var inner in PropertyNames(member.Value)) yield return inner;
                }
            }
        }
        else if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schema.EnumerateArray())
                foreach (var inner in PropertyNames(item)) yield return inner;
        }
    }
}
