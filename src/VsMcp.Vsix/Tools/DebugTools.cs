using VsMcp.Services.VisualStudio;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using VsMcp.Contracts;
using VsMcp.Mcp;

namespace VsMcp.Tools
{
    /// <summary>Starts, stops and reports debugging, and brings the Visual Studio window forward.</summary>
    internal sealed class DebugTools
    {
        private readonly VsContext _vs;
        private readonly DTE2 _dte;
        private readonly JoinableTaskFactory _joinableTaskFactory;

        public DebugTools(VsContext vs)
        {
            _vs = vs;
            _dte = vs.Dte;
            _joinableTaskFactory = vs.JoinableTaskFactory;
        }

        [McpServerTool(Name = "get_debug_status", Title = "Get debugger status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
        [Description("Read withoutDebuggingRequest for Ctrl+F5 process confirmation (not waited on by waitSeconds). Read the current solution, active configuration, startup projects, debugger mode, debugged process IDs and paths, and latest launch request phase. requested only means F5 was posted; running/break and process readback prove debugger activity. ended means VS reported return to design mode. No launch is retried automatically. Set waitSeconds to block (event-driven, no polling needed) while a start or stop request is still unresolved: it returns as soon as the debugger enters run/break/design mode, or after waitSeconds with the current status. The default is 10; 0 returns immediately. Only an unresolved start/stop request is waited on; otherwise it returns at once.")]
        public async Task<CallToolResult> GetDebugStatusTool(
            [Description("Maximum seconds to wait for an unresolved start/stop request to resolve. Default 10; 0 returns immediately.")] [Range(0, 120)] int waitSeconds = 10,
            CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var wait = TimeSpan.FromSeconds(Math.Max(0, Math.Min(120, waitSeconds)));
            var status = await _vs.Debug().StatusAsync(_vs.Instance(), wait, cancellationToken);
            status["without_debugging_request"] = _vs.WithoutDebuggingStatus;
            return ToolJson.Result(status);
        }

        [McpServerTool(Name = "start_debugging", Title = "Start debugging", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
        [Description("Start debugging like pressing F5: post F5 to this VS instance and return immediately. It uses the solution's current configuration and startup projects, which this tool never changes (see get_debug_status). VS may build the startup projects and their dependencies according to its own F5 settings; this is separate from build_projects. Rejected while building, already debugging or another launch is unresolved. An accepted request is not launch success: poll get_debug_status for the actual mode and process IDs. A cancelled or failed launch may remain requested; never blindly retry it.")]
        public async Task<CallToolResult> StartDebuggingTool(CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (_vs.TestsRunning()) throw new McpToolException(ToolErrorCode.ExecutionError, "Wait for or cancel the owned test run before debugging.");
            if (_vs.DebugLaunchPending) throw new McpToolException(ToolErrorCode.DebugLaunchPending, "A launch is unresolved; inspect get_debug_status.");
            return ToolJson.Result(_vs.Debug().Start(_vs.Instance()));
        }

        [McpServerTool(Name = "start_without_debugging", Title = "Start without debugging", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
        [Description("Launch like Ctrl+F5 using the current configuration and startup projects. VS may build dependencies according to its own settings. Rejects builds, active tests/debugging and unresolved launches. Supports startup projects exposing one executable via the active Built output group (managed DLL outputs require an EXE apphost). External programs, browser and dotnet-hosted launches are unsupported. Returns started only after observing new VS descendant processes matching every expected path, including PID/path; this does not prove application readiness. Otherwise returns unconfirmed, never success on command acceptance. Observation continues for 120 seconds despite cancellation; inspect get_debug_status.withoutDebuggingRequest. Unconfirmed requests block further launches; use without_debugging_reconcile to recheck or explicitly abandon tracking, never blindly retry. stop_debugging does not terminate these processes.")]
        public async Task<CallToolResult> StartWithoutDebuggingTool(
            [Description("Seconds to wait for actual process confirmation; default 30. Observation continues independently up to 120 seconds.")] [Range(0, 120)] int waitSeconds = 30,
            CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (_vs.TestsRunning()) throw new McpToolException(ToolErrorCode.ExecutionError, "Wait for or cancel the owned test run before launching.");
            return ToolJson.Result(await _vs.WithoutDebugging().StartAsync(Math.Max(0, Math.Min(120, waitSeconds)), cancellationToken));
        }

        [McpServerTool(Name = "without_debugging_reconcile", Title = "Reconcile a Ctrl+F5 request", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
        [Description("Recheck the exact Ctrl+F5 request from get_debug_status.withoutDebuggingRequest.requestId using a fresh process snapshot, even after background observation expired. Never posts another launch. By default an unconfirmed request keeps blocking launches. After inspecting VS, explicitly set abandonTracking and acknowledgeLaunchMayStillOccur to true to release an unresolved request. Abandoning tracking does NOT cancel a queued VS command, prove launch failure, or terminate processes; a late launch remains possible. Active builds/tests/debugging prevent abandonment. A newly confirmed request stays started rather than abandoned. Stale request IDs fail without changing the current request.")]
        public async Task<CallToolResult> ReconcileWithoutDebuggingAsync(
            [Description("Exact requestId returned by start_without_debugging or get_debug_status.")] string requestId,
            [Description("Explicitly release an unconfirmed request after rechecking. Does not cancel the VS command.")] bool abandonTracking = false,
            [Description("Must be true with abandonTracking: a queued command may still launch a program.")] bool acknowledgeLaunchMayStillOccur = false,
            CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            try { return ToolJson.Result(await _vs.WithoutDebugging().ReconcileAsync(requestId, abandonTracking, acknowledgeLaunchMayStillOccur, cancellationToken)); }
            catch (ArgumentException error) { throw new McpToolException(ToolErrorCode.InvalidArgument, error.Message); }
        }

        [McpServerTool(Name = "stop_debugging", Title = "Stop debugging", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
        [Description("Stop the current debug session, like Stop Debugging in Visual Studio (may terminate launched apps or detach attached processes). Runs asynchronously without compiling code; poll get_debug_status for design mode, empty debugged processes and the stopRequest phase. A queued stop never applies to a later session. Already idle is a no-op. Does not kill processes directly.")]
        public async Task<CallToolResult> StopDebuggingTool(CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return ToolJson.Result(_vs.Debug().Stop(_vs.Instance()));
        }
    }
}
