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
        [Description("Read the current solution, active configuration, startup projects, debugger mode, debugged process IDs and paths, and latest launch request phase. requested only means F5 was posted; running/break and process readback prove debugger activity. ended means VS reported return to design mode. No launch is retried automatically. Set waitSeconds to block (event-driven, no polling needed) while a start or stop request is still unresolved: it returns as soon as the debugger enters run/break/design mode, or after waitSeconds with the current status. The default is 10; 0 returns immediately. Only an unresolved start/stop request is waited on; otherwise it returns at once.")]
        public async Task<CallToolResult> GetDebugStatusTool(
            [Description("Maximum seconds to wait for an unresolved start/stop request to resolve. Default 10; 0 returns immediately.")] [Range(0, 120)] int waitSeconds = 10,
            CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var wait = TimeSpan.FromSeconds(Math.Max(0, Math.Min(120, waitSeconds)));
            return ToolJson.Result(await _vs.Debug().StatusAsync(_vs.Instance(), wait, cancellationToken));
        }

        [McpServerTool(Name = "start_debugging", Title = "Start debugging", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
        [Description("Start debugging like pressing F5: post F5 to this VS instance and return immediately. It uses the solution's current configuration and startup projects, which this tool never changes (see get_debug_status). VS may build the startup projects and their dependencies according to its own F5 settings; this is separate from build_projects. Rejected while building, already debugging or another launch is unresolved. An accepted request is not launch success: poll get_debug_status for the actual mode and process IDs. A cancelled or failed launch may remain requested; never blindly retry it.")]
        public async Task<CallToolResult> StartDebuggingTool(CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (_vs.TestsRunning()) throw new McpToolException(ToolErrorCode.ExecutionError, "Wait for or cancel the owned test run before debugging.");
            return ToolJson.Result(_vs.Debug().Start(_vs.Instance()));
        }

        [McpServerTool(Name = "stop_debugging", Title = "Stop debugging", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
        [Description("Stop the current debug session, like Stop Debugging in Visual Studio (may terminate launched apps or detach attached processes). Runs asynchronously without compiling code; poll get_debug_status for design mode, empty debugged processes and the stop_request phase. A queued stop never applies to a later session. Already idle is a no-op. Does not kill processes directly.")]
        public async Task<CallToolResult> StopDebuggingTool(CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return ToolJson.Result(_vs.Debug().Stop(_vs.Instance()));
        }
    }
}
