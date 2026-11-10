using System.Threading;
using System.Threading.Tasks;

namespace VsMcp.Services.Testing
{
    internal sealed class TestExecutionRequest
    {
        public string[] Sources { get; set; }
        public string Filter { get; set; }
        public string SettingsPath { get; set; }
        public string AdapterPath { get; set; }
        public string Platform { get; set; } = "x64";
        public string ResultsDirectory { get; set; }
        public bool Discover { get; set; }
        public string DiscoveryPath { get; set; }
    }

    internal sealed class TestExecutionResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
    }

    internal interface ITestExecutor
    {
        Task<TestExecutionResult> ExecuteAsync(TestExecutionRequest request, CancellationToken cancellationToken);
    }
}
