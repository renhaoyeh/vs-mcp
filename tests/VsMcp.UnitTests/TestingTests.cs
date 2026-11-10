using VsMcp.Contracts;
using VsMcp.Infrastructure;
using VsMcp.Services.Testing;
using Xunit;

namespace VsMcp.UnitTests;

public sealed class TestingTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "vs-mcp-unit-" + Guid.NewGuid().ToString("N"));
    private readonly string source;
    public TestingTests() { Directory.CreateDirectory(folder); source = Path.Combine(folder, "test.dll"); File.WriteAllText(source, "fixture"); }
    public void Dispose() => Directory.Delete(folder, true);
    private static string Trx(int executed, int passed, int failed) => $"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="passes" outcome="Passed" duration="00:00:00.1" />
            <UnitTestResult testName="fails" outcome="Failed"><Output><ErrorInfo><Message>expected 1, got 2</Message><StackTrace>Example.cs:line 42</StackTrace></ErrorInfo></Output></UnitTestResult>
          </Results>
          <ResultSummary><Counters total="2" executed="{executed}" passed="{passed}" failed="{failed}" /></ResultSummary>
        </TestRun>
        """;

    private sealed class Executor(Func<TestExecutionRequest, CancellationToken, Task<TestExecutionResult>> action) : ITestExecutor
    { public Task<TestExecutionResult> ExecuteAsync(TestExecutionRequest request, CancellationToken token) => action(request, token); }

    [Fact]
    public void Trx_keeps_failure_details_and_prioritizes_failures_when_bounded()
    {
        var path = Path.Combine(folder, "result.trx"); File.WriteAllText(path, Trx(2, 1, 1));
        var report = TrxReportReader.Read(path, 1);
        Assert.Equal(1, report.Failed); Assert.True(report.Truncated);
        Assert.Equal("fails", report.Results.Single().Name);
        Assert.Contains("expected 1", report.Results[0].ErrorMessage);
        Assert.Contains("line 42", report.Results[0].StackTrace);
    }
    [Fact]
    public void Trx_rejects_dtd_and_missing_counters()
    {
        var path = Path.Combine(folder, "bad.trx");
        File.WriteAllText(path, "<!DOCTYPE r [<!ENTITY secret SYSTEM 'file:///C:/Windows/win.ini'>]><r>&secret;</r>");
        Assert.Throws<System.Xml.XmlException>(() => TrxReportReader.Read(path, 100));
        File.WriteAllText(path, "<TestRun/>"); Assert.Throws<InvalidDataException>(() => TrxReportReader.Read(path, 100));
    }
    [Fact]
    public async Task Nonzero_exit_with_failed_tests_is_failed_and_keeps_report()
    {
        using var service = new TestRunService(new Executor((request, token) =>
        { File.WriteAllText(Path.Combine(request.ResultsDirectory, "results.trx"), Trx(2, 1, 1)); return Task.FromResult(new TestExecutionResult { ExitCode = 1, Output = "failed" }); }));
        var id = service.Start([source], null, null, null);
        var result = await service.StatusAsync(id, 10, 1, default);
        Assert.Equal(TestRunStatus.Failed, result.Status); Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, result.Report.Failed); Assert.True(result.Report.Truncated);
    }
    [Fact]
    public async Task No_executed_tests_never_reports_passed()
    {
        using var service = new TestRunService(new Executor((request, token) =>
        { File.WriteAllText(Path.Combine(request.ResultsDirectory, "results.trx"), Trx(0, 0, 0)); return Task.FromResult(new TestExecutionResult { ExitCode = 0 }); }));
        var result = await service.StatusAsync(service.Start([source], null, null, null), 10, 100, default);
        Assert.Equal(TestRunStatus.NoTests, result.Status);
    }
    [Fact]
    public async Task Run_continues_after_wait_disconnect_and_only_its_id_can_cancel_it()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new TestRunService(new Executor(async (request, token) =>
        { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return new TestExecutionResult(); }));
        var id = service.Start([source], null, null, null); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => service.Start([source], null, null, null));
        Assert.Throws<ArgumentException>(() => service.Cancel("wrong-id"));
        using var timeout = new CancellationTokenSource(); timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StatusAsync(id, 10, 100, timeout.Token));
        Assert.Equal(TestRunStatus.Running, (await service.StatusAsync(id, 0, 100, default)).Status);
        Assert.Equal(id, (await service.StatusAsync(null, 0, 100, default)).RunId);
        service.Cancel(id);
        Assert.Equal(TestRunStatus.Cancelled, (await service.StatusAsync(id, 10, 100, default)).Status);
    }
    [Fact]
    public async Task Missing_report_and_runner_failure_are_errors_not_success()
    {
        using var service = new TestRunService(new Executor((request, token) => Task.FromResult(new TestExecutionResult { ExitCode = 0, Output = "missing report" })));
        var result = await service.StatusAsync(service.Start([source], null, null, null), 10, 100, default);
        Assert.Equal(TestRunStatus.Error, result.Status); Assert.NotNull(result.Error); Assert.Equal("missing report", result.Output);
    }

    [Fact]
    public async Task Discovery_uses_machine_readable_names_and_bounds_results()
    {
        var service = new TestDiscoveryService(new Executor((request, token) =>
        {
            Assert.True(request.Discover);
            Assert.EndsWith(".txt", request.DiscoveryPath);
            File.WriteAllLines(request.DiscoveryPath, ["A.Test", "B.Test", "C.Test"]);
            return Task.FromResult(new TestExecutionResult { ExitCode = 0, Output = "diagnostic" });
        }));
        var result = await service.DiscoverAsync([source], null, null, "x64", 2, 5, default);
        Assert.Equal(new[] { "A.Test", "B.Test" }, result.Tests);
        Assert.True(result.Truncated);
        Assert.False(File.Exists(Directory.GetFiles(Path.GetTempPath(), "vs-mcp-discovery-*.txt").FirstOrDefault() ?? ""));
    }

    [Fact]
    public async Task Discovery_rejects_nonzero_exit_and_honors_timeout()
    {
        var failed = new TestDiscoveryService(new Executor((request, token) => Task.FromResult(new TestExecutionResult { ExitCode = 1, Output = "adapter unavailable" })));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.DiscoverAsync([source], null, null, "x64", 10, 5, default));
        Assert.Contains("adapter unavailable", error.Message);

        var slow = new TestDiscoveryService(new Executor(async (request, token) => { await Task.Delay(Timeout.Infinite, token); return new TestExecutionResult(); }));
        await Assert.ThrowsAsync<TimeoutException>(() => slow.DiscoverAsync([source], null, null, "x64", 10, 1, default));
    }
    [Theory]
    [InlineData("C:\\folder with spaces\\test.dll", "\"C:\\folder with spaces\\test.dll\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("trailing\\", "\"trailing\\\\\"")]
    public void Runner_quotes_argv_without_shell_interpretation(string input, string expected) => Assert.Equal(expected, VstestExecutor.Quote(input));
}
