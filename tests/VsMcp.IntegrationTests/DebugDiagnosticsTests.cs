using System.Text.Json;
using ModelContextProtocol.Client;
using Xunit;

namespace VsMcp.IntegrationTests;

[Collection("Native")]
public sealed class DebugDiagnosticsTests(NativeSession session)
{
    [Fact]
    public async Task Saves_and_debugs_a_native_dump_with_symbols_modules_and_source_locations()
    {
        var client = session.Client;
        var source = Path.Combine(session.SolutionDirectory, "NativeDebug", "main.cpp");
        var dump = Path.Combine(session.SolutionDirectory, "paused.dmp");
        await client.Call<JsonElement>("startup_project_set", new { project = "NativeDebug" });
        Assert.Equal(BuildStatus.Succeeded, (await client.Call<BuildOutput>("build_projects", new { projects = new[] { "NativeDebug" } })).Status);
        var configuration = await client.Call<JsonElement>("get_solution_configurations");
        var startups = await client.Call<JsonElement>("startup_project_get");
        var symbols = await client.Call<DebugSymbolSettingsOutput>("debugger_get_symbol_settings");
        await client.Call<JsonElement>("debugger_add_breakpoint", new { path = source, line = 4 });
        try
        {
            await client.Call<JsonElement>("start_debugging");
            var live = await WaitForMode(client, "dbgBreakMode");
            var process = live.GetProperty("processes").EnumerateArray().Single();
            var pid = process.GetProperty("pid").GetInt32();
            var executable = process.GetProperty("path").GetString()!;
            var modules = await client.Call<DebugModulesOutput>("debugger_list_modules");
            Assert.Equal(pid, modules.ProcessId);
            Assert.Contains(modules.Items, m => m.Name?.Contains("NativeDebug", StringComparison.OrdinalIgnoreCase) == true);
            var bounded = await client.Call<DebugModulesOutput>("debugger_list_modules", new { maxModules = 1 });
            Assert.Single(bounded.Items);
            Assert.True(bounded.Truncated);
            var observed = await client.Call<DebugExceptionOutput>("debugger_get_exception");
            Assert.Equal(pid, observed.ProcessId);
            var saved = await client.Call<DebugDumpSaved>("debugger_save_dump", new { path = dump, processId = pid });
            Assert.True(saved.Length >= 32);
            Assert.Equal("MDMP", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(dump), 0, 4));
            var bytes = new FileInfo(dump).Length;
            Assert.Contains("INVALID_ARGUMENT", await client.Fail("debugger_save_dump", new { path = dump, processId = pid }));
            Assert.Equal(bytes, new FileInfo(dump).Length);
            await client.Call<JsonElement>("stop_debugging");
            await WaitForMode(client, "dbgDesignMode");
            await client.Call<DebugSymbolSettingsOutput>("debugger_set_symbol_settings", new
            {
                symbolPath = Path.GetDirectoryName(executable), symbolPathState = "1", onlyLoadSymbolsManually = false,
            });
            var opened = await client.Call<JsonElement>("debugger_open_dump", new { path = dump });
            Assert.Equal(dump, opened.GetProperty("dumpRequest").GetProperty("path").GetString());
            await WaitForMode(client, "dbgBreakMode");
            var dumpModules = await client.Call<DebugModulesOutput>("debugger_list_modules");
            Assert.Contains(dumpModules.Items, m => m.Name?.Contains("NativeDebug", StringComparison.OrdinalIgnoreCase) == true);
            Assert.Equal(saved.Instance.Pid, dumpModules.Instance.Pid);
            await client.Call<DebugExceptionOutput>("debugger_get_exception");
            var stack = (await client.Call<JsonElement>("debugger_get_callstack")).GetProperty("data").GetProperty("frames");
            Assert.Contains(stack.EnumerateArray(), f => f.GetProperty("sourcePath").GetString()?.EndsWith("main.cpp", StringComparison.OrdinalIgnoreCase) == true && f.GetProperty("sourceLine").GetUInt32() > 0);
            Assert.Equal(configuration.GetRawText(), (await client.Call<JsonElement>("get_solution_configurations")).GetRawText());
            Assert.Equal(startups.GetRawText(), (await client.Call<JsonElement>("startup_project_get")).GetRawText());
        }
        finally
        {
            var status = await client.Call<JsonElement>("get_debug_status", new { waitSeconds = 30 });
            if (status.GetProperty("request").ValueKind == JsonValueKind.Object)
                Assert.NotEqual("requested", status.GetProperty("request").GetProperty("phase").GetString());
            await client.Call<JsonElement>("stop_debugging");
            await WaitForMode(client, "dbgDesignMode");
            await client.Call<JsonElement>("debugger_remove_breakpoint", new { path = source, line = 4 });
            var restored = await client.Call<DebugSymbolSettingsOutput>("debugger_set_symbol_settings", new
            {
                symbolPath = symbols.SymbolPath, symbolPathState = symbols.SymbolPathState,
                symbolCachePath = symbols.SymbolCachePath, onlyLoadSymbolsManually = symbols.OnlyLoadSymbolsManually,
            });
            Assert.Equal(symbols.SymbolPath, restored.SymbolPath);
            Assert.Equal(symbols.OnlyLoadSymbolsManually, restored.OnlyLoadSymbolsManually);
        }
    }

    [Fact]
    public async Task Reads_the_native_access_violation_event_and_preserves_it_in_a_dump()
    {
        var client = session.Client;
        var source = Path.Combine(session.SolutionDirectory, "NativeDebug", "main.cpp");
        var original = await File.ReadAllTextAsync(source);
        var dump = Path.Combine(session.SolutionDirectory, "exception.dmp");
        try
        {
            // Only the owned temporary fixture changes; the repository fixture stays intact.
            await File.WriteAllTextAsync(source, "int main() { volatile int* target = nullptr; *target = 1; return 0; }");
            await client.Call<JsonElement>("startup_project_set", new { project = "NativeDebug" });
            Assert.Equal(BuildStatus.Succeeded, (await client.Call<BuildOutput>("build_projects", new { projects = new[] { "NativeDebug" }, operation = "rebuild" })).Status);
            await client.Call<JsonElement>("start_debugging");
            var live = await WaitForMode(client, "dbgBreakMode");
            var pid = live.GetProperty("processes").EnumerateArray().Single().GetProperty("pid").GetInt32();
            var exception = await client.Call<DebugExceptionOutput>("debugger_get_exception");
            Assert.NotNull(exception.LastObservedException);
            Assert.Equal("0xC0000005", exception.LastObservedException.Code);
            Assert.Equal(pid, exception.LastObservedException.ProcessId);
            await client.Call<DebugDumpSaved>("debugger_save_dump", new { path = dump, processId = pid });
            await client.Call<JsonElement>("stop_debugging");
            await WaitForMode(client, "dbgDesignMode");
            await client.Call<JsonElement>("debugger_open_dump", new { path = dump });
            await WaitForMode(client, "dbgBreakMode");
            var recorded = await client.Call<DebugExceptionOutput>("debugger_get_exception");
            Assert.NotNull(recorded.LastObservedException);
            Assert.Equal("0xC0000005", recorded.LastObservedException.Code);
        }
        finally
        {
            var state = await client.Call<JsonElement>("get_debug_status", new { waitSeconds = 30 });
            if (state.GetProperty("request").ValueKind == JsonValueKind.Object)
                Assert.NotEqual("requested", state.GetProperty("request").GetProperty("phase").GetString());
            await client.Call<JsonElement>("stop_debugging");
            await WaitForMode(client, "dbgDesignMode");
            await File.WriteAllTextAsync(source, original);
        }
    }

    [Fact]
    public async Task Invalid_dump_and_symbol_arguments_do_not_start_a_session_or_change_settings()
    {
        var client = session.Client;
        var path = Path.Combine(session.SolutionDirectory, "invalid.dmp");
        await File.WriteAllTextAsync(path, "This is not a dump.");
        var original = await client.Call<DebugSymbolSettingsOutput>("debugger_get_symbol_settings");
        Assert.Contains("INVALID_ARGUMENT", await client.Fail("debugger_open_dump", new { path }));
        Assert.Contains("INVALID_ARGUMENT", await client.Fail("debugger_open_dump", new { path = "relative.dmp" }));
        Assert.Contains("INVALID_ARGUMENT", await client.Fail("debugger_set_symbol_settings", new { symbolPath = session.SolutionDirectory }));
        Assert.Contains("INVALID_ARGUMENT", await client.Fail("debugger_set_symbol_settings", new { symbolPath = session.SolutionDirectory, symbolPathState = "11" }));
        var after = await client.Call<DebugSymbolSettingsOutput>("debugger_get_symbol_settings");
        Assert.Equal(original.SymbolPath, after.SymbolPath);
        Assert.Equal(original.SymbolPathState, after.SymbolPathState);
        Assert.Equal("dbgDesignMode", (await client.Call<JsonElement>("get_debug_status")).GetProperty("mode").GetString());
    }

    private static async Task<JsonElement> WaitForMode(McpClient client, string mode)
    {
        for (var i = 0; i < 60; i++)
        {
            var status = await client.Call<JsonElement>("get_debug_status", new { waitSeconds = 1 });
            if (status.GetProperty("mode").GetString() == mode) return status;
            if (mode == "dbgBreakMode" && status.GetProperty("request").GetProperty("phase").GetString() == "failed")
                Assert.Fail("Debug request failed: " + status);
            await Task.Delay(250);
        }
        throw new Xunit.Sdk.XunitException("Debugger did not enter " + mode);
    }
}
