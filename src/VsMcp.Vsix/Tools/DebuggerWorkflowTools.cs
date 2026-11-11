using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using VsMcp.Contracts;
using VsMcp.Mcp;
using VsMcp.Services.Debugging;
using VsMcp.Services.VisualStudio;

namespace VsMcp.Tools
{
    /// <summary>Debugger workflows which need exact context, readback and bounded traversal.</summary>
    internal sealed class DebuggerWorkflowTools
    {
        private readonly VsContext _vs;
        private readonly DebuggerWorkflowService _service;

        public DebuggerWorkflowTools(VsContext vs)
        {
            _vs = vs;
            _service = new DebuggerWorkflowService(vs);
        }

        [McpServerTool(Name = "debugger_processes", Title = "List debugger processes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
        [Description("List bounded local processes visible to the connected debugger and processes already being debugged, including exact PID, image path when accessible, current selection, debug mode, and instance identity. Does not attach or switch processes.")]
        public async Task<CallToolResult> ProcessesAsync([Range(1, 1000)] int maxResults = 100, CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(_service.Processes(maxResults));
        }

        [McpServerTool(Name = "debugger_select_process", Title = "Select debugger process", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
        [Description("Select an exact process already debugged by this Visual Studio instance. Requires the current paused contextId and exact processId/expectedPath from debugger_processes. Does not attach, detach, resume, or terminate. Reads back CurrentProcess and invalidates the prior break context; read threads/call stack again afterward.")]
        public async Task<CallToolResult> SelectProcessAsync(string contextId, int processId, string expectedPath, CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(_service.SelectProcess(contextId, processId, expectedPath));
        }

        [McpServerTool(Name = "debugger_detach_process", Title = "Detach debugger from one process", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Detach the debugger from only the exact debugged PID after verifying its absolute expectedPath. Requires a paused session and idle build/test state. This detaches the debugger and preserves the target process. Returns process readback or pending status; after interruption inspect debugger_processes and never blindly retry.")]
        public async Task<CallToolResult> DetachProcessAsync(int processId, string expectedPath, CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(_service.Detach(processId, expectedPath));
        }

        [McpServerTool(Name = "debugger_variables", Title = "Expand debugger variables", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Expand an expression and bounded DataMembers tree in the selected paused frame, and/or include arguments from the one-based frameIndex in the current thread call stack. Requires contextId/processId/threadId/frameIndex from debugger_get_callstack. Expression expansion requires that frameIndex is the exact selected frame; arguments are read directly from that indexed frame without changing selection. Expression evaluation/getters may execute debuggee code or have side effects. maxDepth is 0..8, maxNodes 1..500. timeoutMilliseconds (1..10000) applies to the root DTE expression evaluation only; argument and child reads do not have a guaranteed overall operation timeout.")]
        public async Task<CallToolResult> VariablesAsync(string contextId, int processId, int threadId, int frameIndex, string expression = "", bool includeArguments = false, [Range(0, 8)] int maxDepth = 2, [Range(1, 500)] int maxNodes = 100, [Range(1, 10000)] int timeoutMilliseconds = 1000, CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(_service.Variables(contextId, processId, threadId, frameIndex, expression, includeArguments, maxDepth, maxNodes, timeoutMilliseconds));
        }

        [McpServerTool(Name = "debugger_breakpoint_add", Title = "Add advanced debugger breakpoint", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
        [Description("Add exactly one file, function, data or address breakpoint with optional condition and hit-count policy. Use kind=file/function/data/address and only the matching location fields. Returns a provider-scoped handle and Visual Studio readback; binding depends on the loaded symbols and debug engine. After interruption inspect debugger_breakpoint_list before retrying.")]
        public async Task<CallToolResult> AddBreakpointAsync(
            string kind,
            string functionName = "",
            string path = "",
            [Range(1, int.MaxValue)] int line = 1,
            [Range(1, int.MaxValue)] int column = 1,
            string language = "",
            string dataExpression = "",
            [Range(1, 100)] int dataCount = 1,
            string address = "",
            string condition = "",
            string conditionType = "whenTrue",
            [Range(0, int.MaxValue)] int hitCount = 0,
            string hitCountType = "none",
            CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(_service.AddBreakpoint(kind, functionName, path, line, column, language, dataExpression, dataCount, address, condition, conditionType, hitCount, hitCountType));
        }

        [McpServerTool(Name = "debugger_breakpoint_list", Title = "List advanced debugger breakpoints", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
        [Description("List bounded top-level debugger breakpoints with exact per-provider handles when this tool added them, location, conditions, hit-count state, pending/bound/error type, bound address and binding errors when exposed by Breakpoint3. Unhandled engine properties can vary.")]
        public async Task<CallToolResult> ListBreakpointsAsync([Range(1, 500)] int maxResults = 100, CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(_service.ListBreakpoints(maxResults));
        }

        [McpServerTool(Name = "debugger_breakpoint_update", Title = "Update one debugger breakpoint", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
        [Description("Enable or disable only the breakpoint identified by its exact handle from debugger_breakpoint_add/list. Reads back the resulting state; the installed EnvDTE interfaces expose conditions as read-only, so this update changes only Enabled. It never selects breakpoints by line or changes sibling breakpoints.")]
        public async Task<CallToolResult> UpdateBreakpointAsync(string handle, bool enabled, CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(_service.UpdateBreakpoint(handle, enabled));
        }

        [McpServerTool(Name = "debugger_breakpoint_remove", Title = "Remove one debugger breakpoint", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
        [Description("Delete only the breakpoint with this exact per-provider handle returned by debugger_breakpoint_add/list. Never deletes by line or removes matching siblings. Reads back that the handle is absent; after interruption inspect debugger_breakpoint_list before retrying.")]
        public async Task<CallToolResult> RemoveBreakpointAsync(string handle, CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(_service.RemoveBreakpoint(handle));
        }

        [McpServerTool(Name = "debugger_exception_policy_get", Title = "Read debugger exception policy", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
        [Description("Discover exception groups with no groupName; discover that group's named settings and exact codes by providing groupName alone; or read one setting by exact groupName plus exceptionName or supported code. Uses bounded Count/Item access, never COM enumeration. maxResults is 1..500. Unsupported engines fail explicitly.")]
        public async Task<CallToolResult> GetExceptionAsync(string groupName = null, string exceptionName = "", uint? code = null, [Range(1, 500)] int maxResults = 100, CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(_service.GetException(groupName, exceptionName, code, maxResults));
        }

        [McpServerTool(Name = "debugger_exception_policy_set", Title = "Set debugger exception policy", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
        [Description("Set break-when-thrown and/or break-when-user-unhandled on one exact exception setting. Identify it by exact group plus name or a supported numeric code. Uses Debugger4.ExceptionGroups/ExceptionSettings setter methods and reacquires readback because ExceptionSetting objects are snapshots. Requires idle IDE; unsupported groups/engines fail explicitly.")]
        public async Task<CallToolResult> SetExceptionAsync(string groupName, string exceptionName = "", uint? code = null, bool? breakWhenThrown = null, bool? breakWhenUserUnhandled = null, CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(_service.SetException(groupName, exceptionName, code, breakWhenThrown, breakWhenUserUnhandled));
        }

        private CallToolResult Result(object data)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ToolJson.Result(new JObject { ["instance"] = _vs.Instance(), ["data"] = ToolJson.ToJObject(data) });
        }
    }
}
