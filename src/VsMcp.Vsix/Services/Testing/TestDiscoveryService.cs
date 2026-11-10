using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using VsMcp.Contracts;

namespace VsMcp.Services.Testing
{
    internal sealed class TestDiscoveryService
    {
        private readonly ITestExecutor executor;
        public TestDiscoveryService(ITestExecutor executor) { this.executor = executor; }

        public async Task<TestDiscoveryResult> DiscoverAsync(string[] sources, string settingsPath, string adapterPath, string platform, int maxResults, int timeoutSeconds, CancellationToken token)
        {
            if (platform != "x64" && platform != "x86" && platform != "ARM64") throw new ArgumentException("platform must be x64, x86 or ARM64.");
            if (sources == null || sources.Length == 0 || sources.Length > 32 || sources.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path) || !(path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))) throw new ArgumentException("Test sources must be 1-32 existing absolute DLL/EXE paths.");
            if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length) throw new ArgumentException("Duplicate test sources.");
            if (settingsPath != null && (!Path.IsPathRooted(settingsPath) || !File.Exists(settingsPath))) throw new ArgumentException("Settings must be an existing absolute file path.");
            if (adapterPath != null && (!Path.IsPathRooted(adapterPath) || !Directory.Exists(adapterPath))) throw new ArgumentException("Adapter path must be an existing absolute directory.");
            if (maxResults < 1 || maxResults > 1000 || timeoutSeconds < 1 || timeoutSeconds > 120) throw new ArgumentException("maxResults must be 1-1000 and timeoutSeconds 1-120.");

            using (var timer = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token))
            {
                var path = Path.Combine(Path.GetTempPath(), "vs-mcp-discovery-" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    TestExecutionResult result;
                    try { result = await executor.ExecuteAsync(new TestExecutionRequest { Sources = sources.Select(Path.GetFullPath).ToArray(), SettingsPath = settingsPath, AdapterPath = adapterPath, Platform = platform, Discover = true, DiscoveryPath = path }, linked.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && timer.IsCancellationRequested) { throw new TimeoutException("VSTest discovery exceeded timeoutSeconds (" + timeoutSeconds + "). Increase the timeout or check adapter/container load time."); }
                    token.ThrowIfCancellationRequested();
                    if (timer.IsCancellationRequested) throw new TimeoutException("VSTest discovery exceeded timeoutSeconds (" + timeoutSeconds + "). Increase the timeout or check adapter/container load time.");
                    if (result.ExitCode != 0) throw new InvalidOperationException("VSTest discovery failed with exit code " + result.ExitCode + ". " + result.Output);
                    if (!File.Exists(path)) throw new InvalidDataException("VSTest reported success without writing discovered test names. " + result.Output);
                    var tests = new System.Collections.Generic.List<string>(maxResults);
                    bool truncated = false;
                    using (var reader = new StreamReader(path, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (tests.Count == maxResults) { truncated = true; break; }
                            tests.Add(line);
                        }
                    }
                    return new TestDiscoveryResult { Sources = sources.Select(Path.GetFullPath).ToArray(), Tests = tests.ToArray(), Truncated = truncated, ExitCode = result.ExitCode, Output = result.Output.Length > 4000 ? result.Output.Substring(result.Output.Length - 4000) : result.Output };
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }
        }
    }
}
