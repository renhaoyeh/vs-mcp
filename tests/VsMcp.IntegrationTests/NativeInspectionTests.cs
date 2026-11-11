using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace VsMcp.IntegrationTests;

[Collection("Native")]
public sealed class NativeInspectionTests(NativeSession session)
{
    [Fact]
    public async Task Enumerates_installed_debugger_transports_and_engines()
    {
        var inventory = await session.Client.Call<NativeDebuggerInventory>("debugger_list_native_transports");
        Assert.NotEmpty(inventory.Transports);
        Assert.Contains(inventory.Transports, transport => transport.Engines.Count > 0);
    }

    [Fact]
    public async Task Reads_native_memory_at_a_loaded_module_address()
    {
        var client = session.Client;
        var source = Path.Combine(session.SolutionDirectory, "NativeDebug", "main.cpp");
        await PrepareNativeTarget(client, source);
        try
        {
            var processId = await StartAtCounterBreakpoint(client);
            var modules = await client.Call<DebugModulesOutput>("debugger_list_modules");
            var module = Assert.Single(modules.Items, m => m.Name?.Contains("NativeDebug", StringComparison.OrdinalIgnoreCase) == true);
            var baseAddress = Assert.IsType<string>(module.LoadAddress);
            var memory = await client.Call<NativeMemoryOutput>("debugger_read_native_memory", new { processId, address = baseAddress, byteCount = 4 });
            Assert.Equal(processId, memory.ProcessId);
            Assert.Equal(1, memory.FrameIndex);
            Assert.Equal(4, memory.RequestedBytes);
            Assert.True(memory.BytesRead >= 4, $"The debug engine returned only {memory.BytesRead} bytes: {memory.Error}");

            Assert.Contains("INVALID_ARGUMENT", await client.Fail("debugger_read_native_memory", new { processId = 0, address = baseAddress }));
            Assert.Contains("INVALID_ARGUMENT", await client.Fail("debugger_read_native_memory", new { processId, address = "not-hex" }));
            Assert.Contains("INVALID_ARGUMENT", await client.Fail("debugger_read_native_memory", new { processId, address = baseAddress, byteCount = 4097 }));
        }
        finally { await StopAtCounterBreakpoint(client, source); }
    }

    [Fact]
    public async Task Reads_counter_local_through_the_native_frame_property_memory_context()
    {
        var client = session.Client;
        var source = Path.Combine(session.SolutionDirectory, "NativeDebug", "main.cpp");
        await PrepareNativeTarget(client, source);
        try
        {
            var processId = await StartAtCounterBreakpoint(client);
            var memory = await client.Call<NativeMemoryOutput>("debugger_read_native_memory", new
            {
                processId, variableName = "counter", byteCount = 4,
            });
            Assert.Equal(processId, memory.ProcessId);
            Assert.Equal("counter", memory.VariableName);
            Assert.True(memory.BytesRead >= 4, $"The debug engine returned only {memory.BytesRead} bytes: {memory.Error}");
            Assert.Contains("29 00 00 00", memory.HexBytes, StringComparison.OrdinalIgnoreCase);
        }
        finally { await StopAtCounterBreakpoint(client, source); }
    }

    [Fact]
    public async Task Disassembles_the_paused_native_frame()
    {
        var client = session.Client;
        var source = Path.Combine(session.SolutionDirectory, "NativeDebug", "main.cpp");
        await PrepareNativeTarget(client, source);
        try
        {
            var processId = await StartAtCounterBreakpoint(client);
            var disassembly = await client.Call<NativeDisassemblyOutput>("debugger_disassemble", new { processId, maxInstructions = 8 });
            Assert.Equal(processId, disassembly.ProcessId);
            Assert.Equal(1, disassembly.FrameIndex);
            Assert.NotEmpty(disassembly.Instructions);
            Assert.True(disassembly.Instructions.Count <= 8);
        }
        finally { await StopAtCounterBreakpoint(client, source); }
    }

    [Fact]
    public async Task Reads_native_registers_from_the_paused_frame()
    {
        var client = session.Client;
        var source = Path.Combine(session.SolutionDirectory, "NativeDebug", "main.cpp");
        await PrepareNativeTarget(client, source);
        try
        {
            var processId = await StartAtCounterBreakpoint(client);
            var registers = await client.Call<NativeRegistersOutput>("debugger_read_registers", new { processId, maxRegisters = 64 });
            Assert.Equal(processId, registers.ProcessId);
            Assert.Equal(1, registers.FrameIndex);
            Assert.True(registers.Supported, registers.UnsupportedReason);
            Assert.NotEmpty(registers.Registers);
            Assert.Contains(registers.Registers, register => register.Name?.TrimStart('@').Equals("RAX", StringComparison.OrdinalIgnoreCase) == true && !string.IsNullOrWhiteSpace(register.Value));
            Assert.Contains(registers.Registers, register => register.Name?.TrimStart('@').Equals("RIP", StringComparison.OrdinalIgnoreCase) == true && !string.IsNullOrWhiteSpace(register.Value));
        }
        finally { await StopAtCounterBreakpoint(client, source); }
    }

    [Fact]
    public async Task Loads_symbols_for_exact_paused_module_and_reports_actual_symbol_file()
    {
        var client = session.Client;
        var source = Path.Combine(session.SolutionDirectory, "NativeDebug", "main.cpp");
        await PrepareNativeTarget(client, source);
        try
        {
            var processId = await StartAtCounterBreakpoint(client);
            var modules = await client.Call<DebugModulesOutput>("debugger_list_modules");
            var module = Assert.Single(modules.Items, m => m.Name?.Contains("NativeDebug", StringComparison.OrdinalIgnoreCase) == true);
            var modulePath = Assert.IsType<string>(module.Path);
            var pdbPath = Path.ChangeExtension(modulePath, ".pdb");
            Assert.True(File.Exists(pdbPath), "The native fixture's PDB should be beside its built module.");

            try
            {
                var symbols = await client.Call<NativeSymbolsOutput>("debugger_load_module_symbols", new
                {
                    processId, moduleName = module.Name, modulePath, pdbPath,
                });
                Assert.Equal(processId, symbols.ProcessId);
                Assert.Equal(modulePath, symbols.ModulePath, ignoreCase: true);
                Assert.NotNull(symbols.ActualSymbolFile);
                Assert.Contains("does not prove", symbols.Note, StringComparison.OrdinalIgnoreCase);
            }
            catch (XunitException error)
            {
                // The tool request may have partially succeeded before the bridge returned an invocation error.
                // Inspect SymbolFile first; only retry the direct DTE LoadSymbols call if that readback is empty.
                var literal = JsonSerializer.Serialize(pdbPath);
                var diagnosticCode = string.Join("\n", new[]
                {
                    "var oldSuppress = DTE.SuppressUI;",
                    "try {",
                    "    var process = DTE.Debugger.CurrentProcess as EnvDTE90.Process3;",
                    "    if (process == null) return new { error = \"CurrentProcess did not expose Process3.\" };",
                    "    var collection = process.Modules;",
                    "    var found = new System.Collections.Generic.List<EnvDTE90.Module>();",
                    "    for (var index = 1; index <= collection.Count; index++) { var candidate = (EnvDTE90.Module)collection.Item(index); if (string.Equals(candidate.Name, \"NativeDebug.exe\", StringComparison.OrdinalIgnoreCase)) found.Add(candidate); }",
                    "    if (found.Length != 1) return new { error = \"Expected one NativeDebug module.\", moduleCount = found.Length };",
                    "    var module = found[0];",
                    "    var before = module.SymbolFile;",
                    "    string loadError = null;",
                    "    if (string.IsNullOrWhiteSpace(before)) {",
                    "        try { DTE.SuppressUI = true; module.LoadSymbols(" + literal + "); }",
                    "        catch (Exception ex) { loadError = ex.ToString(); }",
                    "    }",
                    "    return new { processId = process.ProcessID, moduleName = module.Name, modulePath = module.Path, before, after = module.SymbolFile, loadError };",
                    "} catch (Exception ex) { return new { error = ex.ToString() }; }",
                    "finally { DTE.SuppressUI = oldSuppress; }",
                });
                var diagnostic = await client.Call<JsonElement>("execute_code", new { code = diagnosticCode });
                throw new XunitException("debugger_load_module_symbols failed: " + error.Message + " DTE diagnostic: " + diagnostic.GetRawText());
            }
        }
        finally { await StopAtCounterBreakpoint(client, source); }
    }

    private async Task PrepareNativeTarget(ModelContextProtocol.Client.McpClient client, string source)
    {
        await client.Call<JsonElement>("startup_project_set", new { project = "NativeDebug" });
        Assert.Equal(BuildStatus.Succeeded, (await client.Call<BuildOutput>("build_projects", new { projects = new[] { "NativeDebug" } })).Status);
        await client.Call<JsonElement>("debugger_add_breakpoint", new { path = source, line = 4 });
    }

    private static async Task<int> StartAtCounterBreakpoint(ModelContextProtocol.Client.McpClient client)
    {
        await client.Call<JsonElement>("start_debugging");
        var live = await WaitForMode(client, "dbgBreakMode");
        return live.GetProperty("processes").EnumerateArray().Single().GetProperty("pid").GetInt32();
    }

    private static async Task StopAtCounterBreakpoint(ModelContextProtocol.Client.McpClient client, string source)
    {
        await client.Call<JsonElement>("stop_debugging");
        await WaitForMode(client, "dbgDesignMode");
        await client.Call<JsonElement>("debugger_remove_breakpoint", new { path = source, line = 4 });
    }

    private static async Task<JsonElement> WaitForMode(ModelContextProtocol.Client.McpClient client, string mode)
    {
        for (var i = 0; i < 60; i++)
        {
            var status = await client.Call<JsonElement>("get_debug_status", new { waitSeconds = 1 });
            if (status.GetProperty("mode").GetString() == mode) return status;
            if (mode == "dbgBreakMode" && status.GetProperty("request").GetProperty("phase").GetString() == "failed")
                Assert.Fail("Debug request failed: " + status);
            await Task.Delay(250);
        }
        throw new XunitException("Debugger did not enter " + mode);
    }
}
