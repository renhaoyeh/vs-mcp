using System.Text.Json;
using System.Runtime.ExceptionServices;
using Xunit;

namespace VsMcp.IntegrationTests;

[Collection("Native")]
public sealed class DebuggerWorkflowTests(NativeSession session)
{
    [Fact]
    public async Task Lists_processes_and_targets_breakpoint_mutations_by_handle()
    {
        var client = session.Client;
        var processes = await client.Call<JsonElement>("debugger_processes");
        var processData = processes.GetProperty("data");
        Assert.True(processes.GetProperty("instance").GetProperty("pid").GetInt32() > 0);
        Assert.Equal("dbgDesignMode", processData.GetProperty("mode").GetString());
        Assert.True(processData.GetProperty("localProcesses").ValueKind == JsonValueKind.Array);
        Assert.Empty(processData.GetProperty("debuggedProcesses").EnumerateArray());

        var info = await client.Call<InstanceInfo>("get_vs_info");
        var source = Path.Combine(Path.GetDirectoryName(info.SolutionPath!)!, "NativeDebug", "main.cpp");
        string? handle = null;
        try
        {
            var added = await client.Call<JsonElement>("debugger_breakpoint_add", new { kind = "file", path = source, line = 4, column = 1, language = "C++" });
            handle = added.GetProperty("data").GetProperty("handle").GetString();
            Assert.False(string.IsNullOrWhiteSpace(handle));

            var listed = await client.Call<JsonElement>("debugger_breakpoint_list");
            var item = listed.GetProperty("data").GetProperty("items").EnumerateArray()
                .Single(b => b.GetProperty("handle").GetString() == handle);
            Assert.Contains("file", item.GetProperty("locationType").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(4, item.GetProperty("line").GetInt32());

            var updated = await client.Call<JsonElement>("debugger_breakpoint_update", new { handle, enabled = false });
            Assert.False(updated.GetProperty("data").GetProperty("breakpoint").GetProperty("enabled").GetBoolean());
            Assert.True(updated.GetProperty("data").GetProperty("readbackConfirmed").GetBoolean());

            var removed = await client.Call<JsonElement>("debugger_breakpoint_remove", new { handle });
            Assert.True(removed.GetProperty("data").GetProperty("removed").GetBoolean());
            handle = null;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(handle))
                await client.Call<JsonElement>("debugger_breakpoint_remove", new { handle });
        }
    }

    [Fact]
    public async Task Selects_and_detaches_one_paused_child_without_terminating_it()
    {
        var client = session.Client;
        await client.Call<JsonElement>("startup_project_set", new { project = "NativeDebug" });
        Assert.Equal(BuildStatus.Succeeded, (await client.Call<BuildOutput>("build_projects", new { projects = new[] { "NativeDebug" } })).Status);
        var launch = await client.Call<JsonElement>("start_without_debugging", new { waitSeconds = 30 });
        var target = Assert.Single(launch.GetProperty("processes").EnumerateArray());
        var pid = target.GetProperty("pid").GetInt32();
        var path = target.GetProperty("path").GetString()!;
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        try
        {
            var attached = await client.Call<JsonElement>("debugger_attach", new { processId = pid, expectedPath = path, engine = "native" });
            Assert.True(attached.GetProperty("data").GetProperty("attached").GetBoolean());
            await client.Call<JsonElement>("debugger_break");
            await WaitForMode(client, "dbgBreakMode");

            var processList = await client.Call<JsonElement>("debugger_processes");
            var context = processList.GetProperty("data").GetProperty("contextId").GetString()!;
            var selection = await client.Call<JsonElement>("debugger_select_process", new { contextId = context, processId = pid, expectedPath = path });
            Assert.True(selection.GetProperty("data").GetProperty("confirmed").GetBoolean());

            var detached = await client.Call<JsonElement>("debugger_detach_process", new { processId = pid, expectedPath = path });
            Assert.Equal("detached", detached.GetProperty("data").GetProperty("status").GetString());
            Assert.False(detached.GetProperty("data").GetProperty("debugged").GetBoolean());
            Assert.False(process.HasExited);
            Assert.Equal(path, process.MainModule!.FileName, ignoreCase: true);
        }
        finally
        {
            await client.Call<JsonElement>("stop_debugging");
            if (!process.HasExited && string.Equals(path, process.MainModule?.FileName, StringComparison.OrdinalIgnoreCase))
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Expands_paused_native_variables_and_function_arguments()
    {
        var client = session.Client;
        await client.Call<JsonElement>("startup_project_set", new { project = "NativeDebug" });
        Assert.Equal(BuildStatus.Succeeded, (await client.Call<BuildOutput>("build_projects", new { projects = new[] { "NativeDebug" } })).Status);
        var source = Path.Combine(session.SolutionDirectory, "NativeDebug", "main.cpp");
        var handles = new List<string>();
        Exception? failure = null;
        try
        {
            var added = await client.Call<JsonElement>("debugger_breakpoint_add", new { kind = "file", path = source, line = 4, language = "C++" });
            handles.Add(added.GetProperty("data").GetProperty("handle").GetString()!);
            await client.Call<JsonElement>("start_debugging");
            await WaitForMode(client, "dbgBreakMode");
            var stack = (await client.Call<JsonElement>("debugger_get_callstack")).GetProperty("data");
            var contextId = stack.GetProperty("contextId").GetString()!;
            var processId = stack.GetProperty("processId").GetInt32();
            var threadId = stack.GetProperty("threadId").GetInt32();
            var variables = await client.Call<JsonElement>("debugger_variables", new { contextId, processId, threadId, frameIndex = 1, expression = "counter", includeArguments = true, maxDepth = 2, maxNodes = 30 });
            var data = variables.GetProperty("data");
            Assert.True(data.GetProperty("evaluationMayExecute").GetBoolean());
            Assert.Contains(data.GetProperty("items").EnumerateArray(), v => v.GetProperty("name").GetString() == "counter" && v.GetProperty("isValidValue").GetBoolean());

            var threads = (await client.Call<JsonElement>("debugger_list_threads")).GetProperty("data");
            JsonElement workerStack = default;
            JsonElement workerFrame = default;
            var workerThreadId = 0;
            foreach (var candidate in threads.GetProperty("items").EnumerateArray().Where(t => !t.GetProperty("isCurrent").GetBoolean()))
            {
                var candidateId = candidate.GetProperty("threadId").GetInt32();
                await client.Call<JsonElement>("debugger_select_thread", new { contextId, processId, threadId = candidateId });
                var candidateStack = (await client.Call<JsonElement>("debugger_get_callstack")).GetProperty("data");
                var candidateFrame = candidateStack.GetProperty("frames").EnumerateArray()
                    .FirstOrDefault(f => f.GetProperty("functionName").GetString()?.Contains("Worker", StringComparison.Ordinal) == true);
                if (candidateFrame.ValueKind == JsonValueKind.Undefined) continue;
                workerThreadId = candidateId;
                workerStack = candidateStack;
                workerFrame = candidateFrame;
                break;
            }
            Assert.NotEqual(0, workerThreadId);
            var workerFrameIndex = workerFrame.GetProperty("frameIndex").GetInt32();
            var selectedWorkerFrameBefore = workerStack.GetProperty("frames").EnumerateArray()
                .Single(f => f.GetProperty("isCurrent").GetBoolean()).GetProperty("frameIndex").GetInt32();
            var arguments = await client.Call<JsonElement>("debugger_variables", new
            {
                contextId = workerStack.GetProperty("contextId").GetString(),
                processId,
                threadId = workerThreadId,
                frameIndex = workerFrameIndex,
                includeArguments = true,
                maxDepth = 1,
                maxNodes = 20,
            });
            Assert.Contains(arguments.GetProperty("data").GetProperty("items").EnumerateArray(), v => v.GetProperty("name").GetString() == "ready" && v.GetProperty("isValidValue").GetBoolean());
            var workerStackAfterArguments = (await client.Call<JsonElement>("debugger_get_callstack")).GetProperty("data");
            var selectedWorkerFrameAfter = workerStackAfterArguments.GetProperty("frames").EnumerateArray()
                .Single(f => f.GetProperty("isCurrent").GetBoolean()).GetProperty("frameIndex").GetInt32();
            Assert.Equal(selectedWorkerFrameBefore, selectedWorkerFrameAfter);

            // Finish context-sensitive reads before adding other native breakpoint kinds.
            await client.Call<JsonElement>("debugger_select_thread", new { contextId, processId, threadId });
            await client.Call<JsonElement>("debugger_select_frame", new { contextId, processId, threadId, frameIndex = 1 });
            var function = await client.Call<JsonElement>("debugger_breakpoint_add", new { kind = "function", functionName = "Run", language = "C++", hitCount = 1, hitCountType = "equalTo" });
            handles.Add(function.GetProperty("data").GetProperty("handle").GetString()!);
            var dataBreakpoint = await client.Call<JsonElement>("debugger_breakpoint_add", new { kind = "data", dataExpression = "counter", dataCount = 1 });
            handles.Add(dataBreakpoint.GetProperty("data").GetProperty("handle").GetString()!);
            var disassembly = await client.Call<JsonElement>("debugger_disassemble", new { processId, frameIndex = 1, maxInstructions = 8 });
            var address = disassembly.GetProperty("instructions").EnumerateArray().First().GetProperty("address").GetString()!;
            var addressBreakpoint = await client.Call<JsonElement>("debugger_breakpoint_add", new { kind = "address", address });
            handles.Add(addressBreakpoint.GetProperty("data").GetProperty("handle").GetString()!);
            var breakpointList = await client.Call<JsonElement>("debugger_breakpoint_list", new { maxResults = 100 });
            foreach (var handle in handles)
                Assert.Contains(breakpointList.GetProperty("data").GetProperty("items").EnumerateArray(), b => b.GetProperty("handle").GetString() == handle);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { await client.Call<JsonElement>("stop_debugging"); }
            catch (Exception error) { failure ??= error; }
            try
            {
                var existing = await client.Call<JsonElement>("debugger_breakpoint_list", new { maxResults = 100 });
                var liveHandles = new HashSet<string>(existing.GetProperty("data").GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("handle").GetString()).Where(value => !string.IsNullOrWhiteSpace(value))!);
                foreach (var handle in handles.Where(liveHandles.Contains))
                {
                    try { await client.Call<JsonElement>("debugger_breakpoint_remove", new { handle }); }
                    catch (Exception error) { failure ??= error; }
                }
            }
            catch (Exception error) { failure ??= error; }
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task WaitForMode(ModelContextProtocol.Client.McpClient client, string mode)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var state = await client.Call<JsonElement>("get_debug_status", new { waitSeconds = 1 });
            if (state.GetProperty("mode").GetString() == mode) return;
            await Task.Delay(250);
        }
        Assert.Fail("Debugger did not enter " + mode + ".");
    }
}

[Collection("Native")]
public sealed class NativeExceptionPolicyWorkflowTests(NativeSession session)
{
    [Fact]
    public async Task Discovers_exception_groups_and_exact_codes_with_bounded_indexed_access()
    {
        var client = session.Client;
        var groups = await client.Call<JsonElement>("debugger_exception_policy_get", new { maxResults = 100 });
        var groupItems = groups.GetProperty("data").GetProperty("groups").EnumerateArray().ToArray();
        Assert.Contains(groupItems, item => item.GetProperty("name").GetString() == "Win32 Exceptions");

        var win32 = await client.Call<JsonElement>("debugger_exception_policy_get", new { groupName = "Win32 Exceptions", maxResults = 500 });
        Assert.Contains(win32.GetProperty("data").GetProperty("items").EnumerateArray(), item => item.GetProperty("code").GetUInt32() == 0xC0000005);
    }

    [Fact]
    public async Task Sets_and_restores_one_exact_win32_exception_policy()
    {
        var client = session.Client;
        const uint accessViolation = 0xC0000005;
        var original = await client.Call<JsonElement>("debugger_exception_policy_get", new { groupName = "Win32 Exceptions", code = accessViolation });
        var originalThrown = original.GetProperty("data").GetProperty("breakWhenThrown").GetBoolean();
        try
        {
            var changed = await client.Call<JsonElement>("debugger_exception_policy_set", new { groupName = "Win32 Exceptions", code = accessViolation, breakWhenThrown = !originalThrown });
            Assert.True(changed.GetProperty("data").GetProperty("confirmed").GetBoolean());
            Assert.Equal(!originalThrown, (await client.Call<JsonElement>("debugger_exception_policy_get", new { groupName = "Win32 Exceptions", code = accessViolation })).GetProperty("data").GetProperty("breakWhenThrown").GetBoolean());
        }
        finally
        {
            await client.Call<JsonElement>("debugger_exception_policy_set", new { groupName = "Win32 Exceptions", code = accessViolation, breakWhenThrown = originalThrown });
        }
    }
}

[Collection("Debug")]
public sealed class ManagedExceptionPolicyWorkflowTests(DebugSession session)
{
    [Fact]
    public async Task Sets_and_restores_one_exact_managed_exception_policy()
    {
        var client = session.Client;
        const string group = "Common Language Runtime Exceptions";
        const string exceptionName = "System.Exception";
        var original = await client.Call<JsonElement>("debugger_exception_policy_get", new { groupName = group, exceptionName });
        var originalThrown = original.GetProperty("data").GetProperty("breakWhenThrown").GetBoolean();
        try
        {
            var changed = await client.Call<JsonElement>("debugger_exception_policy_set", new { groupName = group, exceptionName, breakWhenThrown = !originalThrown });
            Assert.True(changed.GetProperty("data").GetProperty("confirmed").GetBoolean());
            var readback = await client.Call<JsonElement>("debugger_exception_policy_get", new { groupName = group, exceptionName });
            Assert.Equal(!originalThrown, readback.GetProperty("data").GetProperty("breakWhenThrown").GetBoolean());
        }
        finally
        {
            await client.Call<JsonElement>("debugger_exception_policy_set", new { groupName = group, exceptionName, breakWhenThrown = originalThrown });
        }
    }
}
