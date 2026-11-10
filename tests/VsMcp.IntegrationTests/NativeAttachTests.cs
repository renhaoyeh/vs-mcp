using System.Text.Json;
using Xunit;

namespace VsMcp.IntegrationTests;

[Collection("Native")]
public sealed class NativeAttachTests(NativeSession session)
{
    [Fact]
    public async Task Attaches_with_explicit_native_engine_and_reads_stack()
    {
        var client = session.Client;
        await client.Call<JsonElement>("startup_project_set", new { project = "NativeDebug" });
        Assert.Equal(BuildStatus.Succeeded, (await client.Call<BuildOutput>("build_projects", new { projects = new[] { "NativeDebug" } })).Status);
        var launch = await client.Call<JsonElement>("start_without_debugging", new { waitSeconds = 30 });
        Assert.Equal("started", launch.GetProperty("status").GetString());
        var target = launch.GetProperty("processes").EnumerateArray().Single();
        using var process = System.Diagnostics.Process.GetProcessById(target.GetProperty("pid").GetInt32());
        try
        {
            var result = await client.Call<JsonElement>("debugger_attach", new { processId = process.Id, expectedPath = target.GetProperty("path").GetString(), engine = "native" });
            Assert.True(result.GetProperty("data").GetProperty("attached").GetBoolean());
            Assert.Contains("EXECUTION_ERROR", await client.Fail("debugger_attach", new { processId = process.Id, expectedPath = target.GetProperty("path").GetString() }));
            await client.Call<JsonElement>("debugger_break");
            await WaitForMode("dbgBreakMode");
            var stack = await client.Call<JsonElement>("debugger_get_callstack");
            Assert.NotEmpty(stack.GetProperty("data").GetProperty("frames").EnumerateArray());
        }
        finally
        {
            await client.Call<JsonElement>("stop_debugging");
            await WaitForMode("dbgDesignMode");
            if (!process.HasExited) process.Kill();
        }

        async Task WaitForMode(string mode)
        {
            for (var i = 0; i < 60; i++)
            {
                var status = await client.Call<JsonElement>("get_debug_status", new { waitSeconds = 1 });
                if (status.GetProperty("mode").GetString() == mode) return;
                await Task.Delay(250);
            }
            Assert.Fail("Debugger did not enter " + mode);
        }
    }
}
