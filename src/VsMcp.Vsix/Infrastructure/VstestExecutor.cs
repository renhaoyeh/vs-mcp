using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VsMcp.Services.Testing;

namespace VsMcp.Infrastructure
{
    internal sealed class VstestExecutor : ITestExecutor
    {
        private readonly string executable;
        public VstestExecutor(string path) { executable = path; }

        internal static string Quote(string value)
        {
            // Windows argv quoting; no shell interprets these arguments.
            var output = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                output.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
            }
            return output.Append('\\', slashes * 2).Append('"').ToString();
        }

        public async Task<TestExecutionResult> ExecuteAsync(TestExecutionRequest request, CancellationToken cancellationToken)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("The connected Visual Studio installation has no VSTest runner. Install its testing workload.", executable);
            var arguments = new List<string>();
            foreach (var path in request.Sources) arguments.Add(Quote(path));
            arguments.Add(Quote("/ResultsDirectory:" + request.ResultsDirectory));
            arguments.Add(Quote("/Logger:trx;LogFileName=results.trx"));
            arguments.Add("/InIsolation");
            arguments.Add(Quote("/Platform:" + request.Platform));
            if (!string.IsNullOrEmpty(request.Filter)) arguments.Add(Quote("/TestCaseFilter:" + request.Filter));
            if (request.SettingsPath != null) arguments.Add(Quote("/Settings:" + request.SettingsPath));
            if (request.AdapterPath != null) arguments.Add(Quote("/TestAdapterPath:" + request.AdapterPath));
            var info = new ProcessStartInfo(executable, string.Join(" ", arguments)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(request.Sources[0]) };
            var output = new StringBuilder(); var gate = new object();
            void Append(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (gate) { output.AppendLine(e.Data); if (output.Length > 32000) output.Remove(0, output.Length - 32000); }
            }
            using (var job = new OwnedProcessJob())
            using (var process = new Process { StartInfo = info })
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.OutputDataReceived += Append; process.ErrorDataReceived += Append;
                process.Start();
                try { job.Assign(process); }
                catch { if (!process.HasExited) process.Kill(); throw; }
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                using (cancellationToken.Register(job.Terminate))
                {
                    while (!process.HasExited) await Task.Delay(100).ConfigureAwait(false);
                    // Terminate remaining owned hosts before draining their redirected pipes.
                    job.Terminate();
                    process.WaitForExit();
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (gate) return new TestExecutionResult { ExitCode = process.ExitCode, Output = output.ToString() };
                }
            }
        }
    }
}
