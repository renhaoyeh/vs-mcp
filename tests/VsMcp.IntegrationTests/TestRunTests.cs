using Xunit;
namespace VsMcp.IntegrationTests;

[Collection("Fixture")]
public sealed class TestRunTests(FixtureSession session)
{
    [Fact]
    public async Task Managed_test_results_include_failures_and_filters_and_empty_runs()
    {
        var client = session.Client; var info = await client.Call<InstanceInfo>("get_vs_info");
        var source = Path.Combine(Path.GetDirectoryName(info.SolutionPath!)!, "TestCases", "bin", "Debug", "net472", "TestCases.dll");
        async Task<TestRunResult> Run(string? filter)
        {
            var result = (await client.Call<ToolResponse<TestRunResult>>("test_run", new { sources = new[] { source }, filter, waitSeconds = 120 })).Data;
            if (result.Status == TestRunStatus.Running) result = (await client.Call<ToolResponse<TestRunResult>>("test_status", new { runId = result.RunId, waitSeconds = 120 })).Data;
            return result;
        }
        var failed = await Run("FullyQualifiedName!~WaitForCancel");
        Assert.True(failed.Status == TestRunStatus.Failed, failed.Error + "\n" + failed.Output);
        Assert.Equal(1, failed.Report.Passed); Assert.Equal(1, failed.Report.Failed);
        Assert.Contains(failed.Report.Results, r => r.Outcome == "Failed" && r.ErrorMessage.Contains("Intentional failure") && !string.IsNullOrEmpty(r.StackTrace));
        var passed = await Run("FullyQualifiedName~Passes");
        Assert.True(passed.Status == TestRunStatus.Passed, passed.Error + "\n" + passed.Output);
        Assert.Equal(1, passed.Report.Total);
        var empty = await Run("FullyQualifiedName~DoesNotExist");
        Assert.True(empty.Status == TestRunStatus.NoTests, empty.Error + "\n" + empty.Output);
        var running = (await client.Call<ToolResponse<TestRunResult>>("test_run", new { sources = new[] { source }, filter = "FullyQualifiedName~WaitForCancel", waitSeconds = 0 })).Data!;
        await Task.Delay(3000); // Let the runner start its owned test host before cancelling.
        var recovered = (await client.Call<ToolResponse<TestRunResult>>("test_status")).Data!;
        Assert.Equal(running.RunId, recovered.RunId);
        await client.Call<ToolResponse<TestRunResult>>("test_cancel", new { runId = running.RunId });
        var cancelled = (await client.Call<ToolResponse<TestRunResult>>("test_status", new { runId = running.RunId, waitSeconds = 120 })).Data!;
        Assert.Equal(TestRunStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task Managed_discovery_lists_known_tests_without_running_them()
    {
        var info = await session.Client.Call<InstanceInfo>("get_vs_info");
        var source = Path.Combine(Path.GetDirectoryName(info.SolutionPath!)!, "TestCases", "bin", "Debug", "net472", "TestCases.dll");
        var result = (await session.Client.Call<ToolResponse<TestDiscoveryResult>>("test_discover", new { sources = new[] { source } })).Data!;
        Assert.Contains(result.Tests, name => name.Contains("Passes"));
        Assert.Contains(result.Tests, name => name.Contains("WaitForCancel"));
        var bounded = (await session.Client.Call<ToolResponse<TestDiscoveryResult>>("test_discover", new { sources = new[] { source }, maxResults = 1 })).Data!;
        Assert.Single(bounded.Tests); Assert.True(bounded.Truncated);
    }
}
