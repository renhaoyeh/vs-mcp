using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VsMcp.Contracts;

namespace VsMcp.Services.Testing
{
    // Run state belongs to the extension, not an HTTP request. Disconnects never start a second run.
    internal sealed class TestRunService : IDisposable
    {
        private sealed class Run
        {
            public TestRunResult Result;
            public CancellationTokenSource Cancellation = new CancellationTokenSource();
            public Task Completion;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Run> runs = new Dictionary<string, Run>();
        private readonly ITestExecutor executor;
        private bool disposed;
        public TestRunService(ITestExecutor testExecutor) { executor = testExecutor; }
        public bool IsRunning { get { lock (gate) return runs.Values.Any(r => r.Result.Status == TestRunStatus.Running); } }

        public string Start(string[] sources, string filter, string settingsPath, string adapterPath, string platform = "x64")
        {
            if (platform != "x64" && platform != "x86" && platform != "ARM64") throw new ArgumentException("platform must be x64, x86 or ARM64.");
            if (sources == null || sources.Length == 0 || sources.Length > 32) throw new ArgumentException("Specify 1-32 already-built test DLL/EXE paths. No build or restore is performed.");
            foreach (var path in sources)
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path) || !(path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Test sources must be existing absolute DLL/EXE paths.");
            if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length) throw new ArgumentException("Duplicate test sources.");
            if (settingsPath != null && (!Path.IsPathRooted(settingsPath) || !File.Exists(settingsPath))) throw new ArgumentException("Settings must be an existing absolute file path.");
            if (adapterPath != null && (!Path.IsPathRooted(adapterPath) || !Directory.Exists(adapterPath))) throw new ArgumentException("Adapter path must be an existing absolute directory.");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(TestRunService));
                if (runs.Values.Any(r => r.Result.Status == TestRunStatus.Running)) throw new InvalidOperationException("A test run is already running. Call test_status without runId to recover its identity and collect results.");
                while (runs.Count >= 10)
                {
                    var old = runs.Values.OrderBy(r => r.Result.StartedAtUtc).First(); runs.Remove(old.Result.RunId); old.Cancellation.Dispose();
                }
                var id = Guid.NewGuid().ToString("N");
                var directory = Path.Combine(Path.GetTempPath(), "vs-mcp-tests", id); Directory.CreateDirectory(directory);
                var run = new Run { Result = new TestRunResult { RunId = id, Status = TestRunStatus.Running, StartedAtUtc = DateTime.UtcNow, Sources = sources.Select(Path.GetFullPath).ToArray(), ResultsPath = Path.Combine(directory, "results.trx") } };
                runs.Add(id, run);
                run.Completion = Task.Run(() => ExecuteAsync(run, new TestExecutionRequest { Sources = run.Result.Sources, Filter = filter, SettingsPath = settingsPath, AdapterPath = adapterPath, Platform = platform, ResultsDirectory = directory }));
                return id;
            }
        }

        private async Task ExecuteAsync(Run run, TestExecutionRequest request)
        {
            TestRunStatus status; TestReport report = null; string error = null, output = ""; int? exitCode = null;
            try
            {
                var execution = await executor.ExecuteAsync(request, run.Cancellation.Token).ConfigureAwait(false);
                exitCode = execution.ExitCode; output = execution.Output;
                report = TrxReportReader.Read(run.Result.ResultsPath, 1000);
                status = report.Failed > 0 ? TestRunStatus.Failed : execution.ExitCode != 0 ? TestRunStatus.Error : report.Executed == 0 ? TestRunStatus.NoTests : TestRunStatus.Passed;
                if (status == TestRunStatus.Error) error = "VSTest exited with an error. Inspect output and the TRX report.";
            }
            catch (OperationCanceledException) { status = TestRunStatus.Cancelled; }
            catch (Exception e) { status = TestRunStatus.Error; error = e.Message; }
            lock (gate)
            {
                run.Result.Status = status; run.Result.Report = report; run.Result.Error = error; run.Result.Output = output;
                run.Result.ExitCode = exitCode; run.Result.FinishedAtUtc = DateTime.UtcNow;
            }
        }

        public async Task<TestRunResult> StatusAsync(string runId, int waitSeconds, int maxResults, CancellationToken token)
        {
            if (waitSeconds < 0 || waitSeconds > 120 || maxResults < 1 || maxResults > 1000) throw new ArgumentException("waitSeconds must be 0-120 and maxResults 1-1000.");
            Run run; lock (gate) run = Get(runId ?? runs.Values.OrderByDescending(r => r.Result.StartedAtUtc).FirstOrDefault()?.Result.RunId);
            if (waitSeconds > 0)
            {
                using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    await Task.WhenAny(run.Completion, Task.Delay(TimeSpan.FromSeconds(waitSeconds), delayCancellation.Token)).ConfigureAwait(false);
                    delayCancellation.Cancel(); token.ThrowIfCancellationRequested();
                }
            }
            lock (gate)
            {
                var r = run.Result; var p = r.Report;
                return new TestRunResult { RunId = r.RunId, Status = r.Status, Sources = (string[])r.Sources.Clone(), StartedAtUtc = r.StartedAtUtc, FinishedAtUtc = r.FinishedAtUtc, ExitCode = r.ExitCode, Error = r.Error, Output = r.Output, ResultsPath = r.ResultsPath, CancelRequested = r.CancelRequested,
                    Report = p == null ? null : new TestReport { Total = p.Total, Executed = p.Executed, Passed = p.Passed, Failed = p.Failed, Skipped = p.Skipped, Results = p.Results.Take(maxResults).ToList(), Truncated = p.Truncated || p.Results.Count > maxResults } };
            }
        }
        public void Cancel(string runId)
        {
            lock (gate) { var run = Get(runId); if (run.Result.Status == TestRunStatus.Running) { run.Result.CancelRequested = true; run.Cancellation.Cancel(); } }
        }
        private Run Get(string id) => id != null && runs.TryGetValue(id, out var run) ? run : throw new ArgumentException("Unknown runId. Run history retains the latest ten runs in this IDE session.");
        public void Dispose() { lock (gate) { disposed = true; foreach (var run in runs.Values) run.Cancellation.Cancel(); } }
    }
}
