using Xunit;

namespace VsMcp.IntegrationTests;

/// <summary>
/// Starts and stops a real debug session in a copy of tests/VsMcp.TestProjects/VsMcp.DebugTestSolution: a console application that sleeps for
/// 30 seconds. It stops the sessions it starts.
/// </summary>
[Collection("Debug")]
public sealed class DebugToolsTests(DebugSession session)
{

    private sealed record PhaseInfo(string? Phase);

    private sealed record ProcessInfo(int Pid, string? Path);

    private sealed record DebugStatus(
        string Mode,
        PhaseInfo? Request,
        string SolutionConfiguration,
        string Platform,
        List<string> StartupProjects,
        List<ProcessInfo> Processes,
        PhaseInfo? StopRequest);

    private static async Task<DebugStatus> WaitFor(ModelContextProtocol.Client.McpClient client, Func<DebugStatus, bool> done, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        DebugStatus status;
        do
        {
            // waitSeconds blocks inside VS until the pending request resolves, instead of polling.
            status = await client.Call<DebugStatus>("get_debug_status", new { waitSeconds = 5 });
            if (done(status)) return status;
            await Task.Delay(300);
        }
        while (DateTime.UtcNow < deadline);
        return status;
    }

    [Fact]
    public async Task Starts_without_debugging_and_confirms_real_process()
    {
        var client = session.Client;
        var before = await client.Call<DebugStatus>("get_debug_status", new { waitSeconds = 0 });
        var build = await client.Call<BuildOutput>("build_projects", new { projects = before.StartupProjects, timeoutSeconds = 60 });
        Assert.Equal(BuildStatus.Succeeded, build.Status);
        var accepted = await client.Call<System.Text.Json.JsonElement>("start_without_debugging", new { waitSeconds = 0 });
        Assert.Equal("unconfirmed", accepted.GetProperty("status").GetString());
        var deadline = DateTime.UtcNow.AddSeconds(45);
        System.Text.Json.JsonElement launch;
        do
        {
            var status = await client.Call<System.Text.Json.JsonElement>("get_debug_status", new { waitSeconds = 0 });
            Assert.Equal("dbgDesignMode", status.GetProperty("mode").GetString());
            launch = status.GetProperty("without_debugging_request");
            if (launch.GetProperty("status").GetString() == "started") break;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal("started", launch.GetProperty("status").GetString());
        Assert.Equal(before.SolutionConfiguration, launch.GetProperty("solution_configuration").GetString());
        Assert.Equal(before.Platform, launch.GetProperty("platform").GetString());
        foreach (var item in launch.GetProperty("processes").EnumerateArray())
        {
            using var process = System.Diagnostics.Process.GetProcessById(item.GetProperty("pid").GetInt32());
            Assert.Equal(item.GetProperty("path").GetString(), process.MainModule!.FileName, ignoreCase: true);
            process.Kill();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task Starts_reports_and_stops_a_debug_session()
    {
        var client = session.Client;
        var status = await client.Call<DebugStatus>("get_debug_status");
        Assert.True(status.Mode == "dbgDesignMode" && status.StartupProjects.Count == 1, "The instance must be idle with exactly one startup project.");

        // Build the startup project first, so the launch does not depend on F5's own build.
        var build = await client.Call<BuildOutput>("build_projects", new
        {
            projects = status.StartupProjects,
            timeoutSeconds = 60,
        });
        Assert.Equal(BuildStatus.Succeeded, build.Status);

        var accepted = await client.Call<DebugStatus>("start_debugging");
        Assert.Equal("requested", accepted.Request?.Phase);

        var running = await WaitFor(client, s => s.Mode == "dbgRunMode" && s.Processes.Count > 0, 30);
        Assert.Equal("dbgRunMode", running.Mode);
        var debuggee = Assert.Single(running.Processes);

        try
        {
            // Starting again, or building, is refused while debugging; nothing is posted.
            Assert.Contains("DEBUGGER_ACTIVE:", await client.Fail("start_debugging"));
            Assert.Contains("DEBUGGER_ACTIVE:", await client.Fail("start_without_debugging"));
            Assert.Contains("DEBUGGER_ACTIVE:", await client.Fail("build_projects", new
            {
                projects = status.StartupProjects,
            }));

            var acceptedStop = await client.Call<DebugStatus>("stop_debugging");
            Assert.Contains(acceptedStop.StopRequest?.Phase, new[] { "requested", "stopped" });

            var ended = await WaitFor(client, s => s.Request?.Phase == "ended", 45);
            Assert.Equal("ended", ended.Request?.Phase);
            Assert.Equal("dbgDesignMode", ended.Mode);
            Assert.Equal("stopped", ended.StopRequest?.Phase);
            Assert.Empty(ended.Processes);

            // Nothing is running, so a stop is a no-op.
            var idle = await client.Call<DebugStatus>("stop_debugging");
            Assert.Equal("already_stopped", idle.StopRequest?.Phase);

            // The next session starts and stops again.
            await client.Call<DebugStatus>("start_debugging");
            var second = await WaitFor(client, s => s.Mode == "dbgRunMode" && s.Processes.Count > 0, 25);
            Assert.Equal("dbgRunMode", second.Mode);
            Assert.NotEqual(debuggee.Pid, Assert.Single(second.Processes).Pid);
        }
        finally
        {
            var current = await client.Call<DebugStatus>("get_debug_status");
            if (current.Processes.Count > 0)
            {
                await client.Call<DebugStatus>("stop_debugging");
                var final = await WaitFor(client, s => s.Mode == "dbgDesignMode" && s.Processes.Count == 0, 25);
                Assert.Equal("dbgDesignMode", final.Mode);
            }
        }
    }
}
