using System.Text.Json;
using ModelContextProtocol.Client;
using Xunit;
namespace VsMcp.IntegrationTests;

internal static class DebuggerInspection
{
    private static async Task Wait(McpClient client, string mode)
    {
        for (int i = 0; i < 100; i++)
        {
            var state = await client.Call<JsonElement>("get_debug_status", new { waitSeconds = 1 });
            if (state.GetProperty("mode").GetString() == mode) return;
            await Task.Delay(300);
        }
        throw new Xunit.Sdk.XunitException("Debugger did not enter " + mode);
    }
    public static async Task Run(McpClient client, string project, string relativePath, int breakpointLine)
    {
        var info = await client.Call<InstanceInfo>("get_vs_info");
        var path = Path.Combine(Path.GetDirectoryName(info.SolutionPath!)!, relativePath);
        await client.Call<JsonElement>("startup_project_set", new { project });
        var build = await client.Call<BuildOutput>("build_projects", new { projects = new[] { project } });
        Assert.Equal(BuildStatus.Succeeded, build.Status);
        await client.Call<JsonElement>("debugger_add_breakpoint", new { path, line = breakpointLine });
        try
        {
            ExpHost.Log("debugger inspection: starting " + project);
            await client.Call<JsonElement>("start_debugging"); await Wait(client, "dbgBreakMode");
            ExpHost.Log("debugger inspection: hit breakpoint " + project);
            var value = await client.Call<JsonElement>("debugger_evaluate", new { expression = "counter" });
            Assert.True(value.GetProperty("data").GetProperty("isValidValue").GetBoolean());
            Assert.Equal("41", value.GetProperty("data").GetProperty("value").GetString());
            var locals = await client.Call<JsonElement>("debugger_get_locals");
            Assert.Contains(locals.GetProperty("data").GetProperty("items").EnumerateArray(), e => e.GetProperty("name").GetString() == "counter");
            var stack = await client.Call<JsonElement>("debugger_get_callstack");
            Assert.NotEmpty(stack.GetProperty("data").GetProperty("frames").EnumerateArray());
            var frames = stack.GetProperty("data");
            var contextId = frames.GetProperty("contextId").GetString();
            var processId = frames.GetProperty("processId").GetInt32();
            var threadId = frames.GetProperty("threadId").GetInt32();
            var threads = (await client.Call<JsonElement>("debugger_list_threads")).GetProperty("data");
            Assert.Equal(contextId, threads.GetProperty("contextId").GetString());
            var other = threads.GetProperty("items").EnumerateArray().First(t => !t.GetProperty("isCurrent").GetBoolean() && (project != "Debuggee" || t.GetProperty("name").GetString() == "Worker"));
            var selectedThread = await client.Call<JsonElement>("debugger_select_thread", new { contextId, processId, threadId = other.GetProperty("threadId").GetInt32() });
            Assert.True(selectedThread.GetProperty("data").GetProperty("confirmed").GetBoolean());
            await client.Call<JsonElement>("debugger_select_thread", new { contextId, processId, threadId });
            var invalidThread = await client.Fail("debugger_select_thread", new { contextId, processId, threadId = -1 });
            Assert.Contains("INVALID_ARGUMENT:", invalidThread);
            Assert.Contains("Thread ID", invalidThread);
            Assert.Contains("stale", await client.Fail("debugger_select_frame", new { contextId = "stale", processId, threadId, frameIndex = 1 }));
            Assert.Contains("outside", await client.Fail("debugger_select_frame", new { contextId, processId, threadId, frameIndex = 1000 }));
            // The second frame is Main, where callerValue is in scope instead of counter.
            var selected = await client.Call<JsonElement>("debugger_select_frame", new { contextId, processId, threadId, frameIndex = 2 });
            Assert.True(selected.GetProperty("data").GetProperty("confirmed").GetBoolean());
            var caller = await client.Call<JsonElement>("debugger_evaluate", new { expression = "callerValue" });
            Assert.Equal("17", caller.GetProperty("data").GetProperty("value").GetString());
            await client.Call<JsonElement>("debugger_select_frame", new { contextId, processId, threadId, frameIndex = 1 });
            var selectedStack = (await client.Call<JsonElement>("debugger_get_callstack")).GetProperty("data");
            Assert.True(selectedStack.GetProperty("frames")[0].GetProperty("isCurrent").GetBoolean());
            await client.Call<JsonElement>("debugger_step_over");
            // Step dispatch can briefly leave break mode unchanged; wait for the evaluated value to change.
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(250); await Wait(client, "dbgBreakMode");
                value = await client.Call<JsonElement>("debugger_evaluate", new { expression = "counter" });
                if (value.GetProperty("data").GetProperty("value").GetString() == "42") break;
            }
            Assert.Equal("42", value.GetProperty("data").GetProperty("value").GetString());
            Assert.Contains("stale", await client.Fail("debugger_select_thread", new { contextId, processId, threadId }));
            // The fixture breakpoint has served its purpose; do not let it interrupt the resume check again.
            await client.Call<JsonElement>("breakpoint_enable", new { path, line = breakpointLine, enabled = false });
            await client.Call<JsonElement>("debugger_continue"); await Wait(client, "dbgRunMode");
            ExpHost.Log("debugger inspection: continued " + project);
            await client.Call<JsonElement>("debugger_break"); await Wait(client, "dbgBreakMode");
        }
        catch (Exception error)
        {
            var status = await client.Call<JsonElement>("get_debug_status", new { waitSeconds = 0 });
            var breakpoints = await client.Call<JsonElement>("debugger_list_breakpoints");
            var output = await client.Call<JsonElement>("output_read", new { pane = "Debug", maxCharacters = 8000 });
            throw new Xunit.Sdk.XunitException(error.Message + "\n" + status + "\n" + breakpoints + "\n" + output);
        }
        finally
        {
            await client.Call<JsonElement>("stop_debugging"); await Wait(client, "dbgDesignMode");
            await client.Call<JsonElement>("debugger_remove_breakpoint", new { path, line = breakpointLine });
        }
    }
}

[Collection("Debug")]
public sealed class ManagedDebuggerInspectionTests(DebugSession session)
{
    [Fact] public Task Inspect_managed_debuggee() => DebuggerInspection.Run(session.Client, "Debuggee", Path.Combine("Debuggee", "Program.cs"), 8);
}
[Collection("Native")]
public sealed class NativeDebuggerInspectionTests(NativeSession session)
{
    [Fact] public Task Inspect_native_debuggee() => DebuggerInspection.Run(session.Client, "NativeDebug", Path.Combine("NativeDebug", "main.cpp"), 4);
}
