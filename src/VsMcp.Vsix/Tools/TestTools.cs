using VsMcp.Services.VisualStudio;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VsMcp.Contracts;
using VsMcp.Mcp;
using VsMcp.Services.Testing;

namespace VsMcp.Tools
{
    internal sealed class TestTools
    {
        private readonly VsContext vs;
        private readonly TestRunService tests;
        public TestTools(VsContext context, TestRunService service) { vs = context; tests = service; }

        [McpServerTool(Name = "test_run", Title = "Run built tests", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
        [Description("Run explicitly supplied, already-built C# or native C++ test DLL/EXE containers with the connected Visual Studio installation's VSTest runner. Requires a compatible installed/deployed test adapter. Does not build, restore or change Test Explorer state. Returns a runId and actual TRX results when finished, or running after waitSeconds; collect with test_status. A disconnect does not cancel the run. Modern Microsoft.Testing.Platform-only executables are not supported.")]
        public async Task<ToolResponse<TestRunResult>> RunAsync([MinLength(1), MaxLength(32)] string[] sources, [Description("Optional VSTest TestCaseFilter expression.")] string filter = null, string settingsPath = null, string adapterPath = null, [Description("Test-host architecture: x64, x86 or ARM64. Must match native test binaries.")] string platform = "x64", [Range(0, 120)] int waitSeconds = 10, [Range(1, 1000)] int maxResults = 100, CancellationToken cancellationToken = default)
        {
            if (waitSeconds < 0 || waitSeconds > 120 || maxResults < 1 || maxResults > 1000) throw new McpToolException(ToolErrorCode.InvalidArgument, "Invalid waitSeconds or maxResults.");
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var instance = vs.ReadInstance();
            if (vs.DebugLaunchPending || vs.Dte.Debugger.CurrentMode != EnvDTE.dbgDebugMode.dbgDesignMode || vs.Dte.Solution.SolutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress)
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Wait for the current build/debug operation to finish before running tests.");
            try
            {
                var id = tests.Start(sources, filter, settingsPath, adapterPath, platform);
                return new ToolResponse<TestRunResult> { Instance = instance, Data = await tests.StatusAsync(id, waitSeconds, maxResults, cancellationToken) };
            }
            catch (ArgumentException e) { throw new McpToolException(ToolErrorCode.InvalidArgument, e.Message); }
            catch (InvalidOperationException e) { throw new McpToolException(ToolErrorCode.ExecutionError, e.Message); }
        }

        [McpServerTool(Name = "test_status", Title = "Read test results", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Read or wait for a test_run operation by runId. Omit runId to recover the latest run after a disconnected request. Returns real TRX counts, bounded per-test outcomes, failure messages and stack traces. No matching/executed tests reports noTests rather than passed. The latest ten runs are retained until the IDE exits.")]
        public async Task<ToolResponse<TestRunResult>> StatusAsync(string runId = null, [Range(0, 120)] int waitSeconds = 0, [Range(1, 1000)] int maxResults = 100, CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); var instance = vs.ReadInstance();
            try { return new ToolResponse<TestRunResult> { Instance = instance, Data = await tests.StatusAsync(runId, waitSeconds, maxResults, cancellationToken) }; }
            catch (ArgumentException e) { throw new McpToolException(ToolErrorCode.InvalidArgument, e.Message); }
        }

        [McpServerTool(Name = "test_cancel", Title = "Cancel an owned test run", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Cancel a test_run operation by runId. Only its owned runner/test-host process job is terminated; unrelated Test Explorer runs are untouched. Returns current state; poll test_status until cancelled or another final status.")]
        public async Task<ToolResponse<TestRunResult>> CancelAsync(string runId, CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); var instance = vs.ReadInstance();
            try { tests.Cancel(runId); return new ToolResponse<TestRunResult> { Instance = instance, Data = await tests.StatusAsync(runId, 0, 100, cancellationToken) }; }
            catch (ArgumentException e) { throw new McpToolException(ToolErrorCode.InvalidArgument, e.Message); }
        }
    }
}
