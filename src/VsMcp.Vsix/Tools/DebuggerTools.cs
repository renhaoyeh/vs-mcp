using VsMcp.Services.VisualStudio;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using VsMcp.Mcp;

namespace VsMcp.Tools
{
    internal sealed class DebuggerTools
    {
        private readonly VsContext vs;
        private EnvDTE.Program _contextProgram;
        public DebuggerTools(VsContext context) { vs = context; }
        private CallToolResult Result(object data) { ThreadHelper.ThrowIfNotOnUIThread(); return ToolJson.Result(new JObject { ["instance"] = vs.Instance(), ["data"] = JToken.Parse(System.Text.Json.JsonSerializer.Serialize(data, ToolJson.Options)) }); }
        private void Paused() {
            ThreadHelper.ThrowIfNotOnUIThread(); if (vs.Dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode) throw new InvalidOperationException("Debugger must be paused at a breakpoint."); }
        private async Task<CallToolResult> CommandAsync(string command, bool paused, CancellationToken token)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (paused) Paused(); else if (vs.Dte.Debugger.CurrentMode != dbgDebugMode.dbgRunMode) throw new InvalidOperationException("Debugger must be running.");
            vs.Debug().BreakContext.Invalidate();
            // Invoke the debugger API directly: command names/availability differ between native and managed engines.
            switch (command)
            {
                case "Debug.Start": vs.Dte.Debugger.Go(false); break;
                case "Debug.BreakAll": vs.Dte.Debugger.Break(false); break;
                case "Debug.StepOver": vs.Dte.Debugger.StepOver(false); break;
                case "Debug.StepInto": vs.Dte.Debugger.StepInto(false); break;
                case "Debug.StepOut": vs.Dte.Debugger.StepOut(false); break;
                default: throw new ArgumentException("Unknown debugger action.");
            }
            return Result(new { accepted = true, mode = vs.Dte.Debugger.CurrentMode.ToString() });
        }
        [McpServerTool(Name = "debugger_continue", Title = "debugger_continue"), Description("Continue a paused debug session. Rejected in design mode, so this never starts a new session.")]
        public Task<CallToolResult> ContinueAsync(CancellationToken cancellationToken = default) => CommandAsync("Debug.Start", true, cancellationToken);
        [McpServerTool(Name = "debugger_break", Title = "debugger_break"), Description("Request a running debug session to pause. Acceptance does not prove it has paused.")]
        public Task<CallToolResult> BreakAsync(CancellationToken cancellationToken = default) => CommandAsync("Debug.BreakAll", false, cancellationToken);
        [McpServerTool(Name = "debugger_step_over", Title = "debugger_step_over"), Description("Step over in a paused debug session.")]
        public Task<CallToolResult> OverAsync(CancellationToken cancellationToken = default) => CommandAsync("Debug.StepOver", true, cancellationToken);
        [McpServerTool(Name = "debugger_step_into", Title = "debugger_step_into"), Description("Step into in a paused debug session.")]
        public Task<CallToolResult> IntoAsync(CancellationToken cancellationToken = default) => CommandAsync("Debug.StepInto", true, cancellationToken);
        [McpServerTool(Name = "debugger_step_out", Title = "debugger_step_out"), Description("Step out in a paused debug session.")]
        public Task<CallToolResult> OutAsync(CancellationToken cancellationToken = default) => CommandAsync("Debug.StepOut", true, cancellationToken);
        private string ContextId()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Paused();
            var program = vs.Dte.Debugger.CurrentProgram;
            if (!SameComObject(program, _contextProgram))
            {
                vs.Debug().BreakContext.Invalidate();
                _contextProgram = program;
            }
            return vs.Debug().BreakContext.Id;
        }

        private void RequireContext(string contextId, int processId, int? threadId = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Paused();
            ContextId();
            try { vs.Debug().BreakContext.Require(contextId); }
            catch (ArgumentException error) { throw new McpToolException(ToolErrorCode.InvalidArgument, error.Message); }
            if (vs.Dte.Debugger.CurrentProcess?.ProcessID != processId ||
                (threadId.HasValue && vs.Dte.Debugger.CurrentThread?.ID != threadId.Value))
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Current debug process/thread changed; read debugger_list_threads or debugger_get_callstack again.");
        }

        private static bool SameComObject(object left, object right)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (left == null || right == null) return left == right;
            if (ReferenceEquals(left, right)) return true;
            var first = Marshal.GetIUnknownForObject(left);
            try
            {
                var second = Marshal.GetIUnknownForObject(right);
                try { return first == second; }
                finally { Marshal.Release(second); }
            }
            finally { Marshal.Release(first); }
        }

        [McpServerTool(Name = "debugger_list_threads", Title = "List debugger threads", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
        [Description("List bounded threads in the current debug program while paused, with processId, threadId, name, isCurrent and contextId. Does not switch processes/programs, freeze threads or resume execution. contextId expires on continue/step or debugger mode changes; pass it to selection tools. Engine-dependent thread enumeration failures are reported, never replaced with OS process threads.")]
        public async Task<CallToolResult> ThreadsAsync([Range(1, 1000)] int maxThreads = 100, CancellationToken cancellationToken = default)
        {
            if (maxThreads < 1 || maxThreads > 1000) throw new ArgumentOutOfRangeException(nameof(maxThreads));
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var contextId = ContextId();
            var debugger = vs.Dte.Debugger;
            var processId = debugger.CurrentProcess.ProcessID;
            var program = debugger.CurrentProgram ?? throw new InvalidOperationException("No current debug program.");
            var threads = program.Threads;
            var items = threads.Cast<EnvDTE.Thread>().Take(maxThreads).Select(t => {
                ThreadHelper.ThrowIfNotOnUIThread();
                return new { threadId = t.ID, name = t.Name, isCurrent = SameComObject(t, debugger.CurrentThread) };
            }).ToArray();
            RequireContext(contextId, processId);
            return Result(new { contextId, processId, program = program.Name,
                items, truncated = threads.Count > maxThreads });
        }

        [McpServerTool(Name = "debugger_select_thread", Title = "Select debugger thread", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
        [Description("Select an exact thread ID in the current paused debug program, using processId and contextId from debugger_list_threads. Never attaches, switches debug processes/programs, freezes threads or resumes execution. Stale contexts and unknown/ambiguous threads fail before changing selection. Reads back the selected thread to confirm the change. Read debugger_get_callstack after switching threads before selecting a frame.")]
        public async Task<CallToolResult> SelectThreadAsync(string contextId, int processId, int threadId, CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            RequireContext(contextId, processId);
            var debugger = vs.Dte.Debugger;
            var matches = debugger.CurrentProgram.Threads.Cast<EnvDTE.Thread>().Where(t => { ThreadHelper.ThrowIfNotOnUIThread(); return t.ID == threadId; }).ToArray();
            if (matches.Length != 1) throw new McpToolException(ToolErrorCode.InvalidArgument, "Thread ID must match exactly one thread in the current debug program.");
            debugger.CurrentThread = matches[0];
            RequireContext(contextId, processId, threadId);
            if (!SameComObject(debugger.CurrentThread, matches[0])) throw new InvalidOperationException("Thread selection was not confirmed. Read debugger_list_threads before retrying.");
            return Result(new { confirmed = true, contextId, processId, threadId = debugger.CurrentThread.ID });
        }

        [McpServerTool(Name = "debugger_select_frame", Title = "Select debugger stack frame", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
        [Description("Select a one-based frameIndex from debugger_get_callstack in the same paused context, process and thread. Stale context/process/thread and invalid indices fail before selection. Reads back frame depth in the same paused context to confirm the change. Never resumes execution or changes the instruction pointer; later locals/evaluation use the selected frame. Selection availability depends on the debug engine.")]
        public async Task<CallToolResult> SelectFrameAsync(string contextId, int processId, int threadId, [Range(1, 1000)] int frameIndex, CancellationToken cancellationToken = default)
        {
            if (frameIndex < 1 || frameIndex > 1000) throw new McpToolException(ToolErrorCode.InvalidArgument, "Frame index must be between 1 and 1000.");
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            RequireContext(contextId, processId, threadId);
            var debugger = vs.Dte.Debugger;
            var target = debugger.CurrentThread.StackFrames.Cast<StackFrame>().Skip(frameIndex - 1).FirstOrDefault();
            if (target == null) throw new McpToolException(ToolErrorCode.InvalidArgument, "Frame index is outside the current call stack.");
            var expected = target as EnvDTE90a.StackFrame2 ?? throw new McpToolException(ToolErrorCode.NotSupported, "This debug engine does not expose stack frame depth.");
            var depth = expected.Depth;
            debugger.CurrentStackFrame = target;
            RequireContext(contextId, processId, threadId);
            var actual = debugger.CurrentStackFrame;
            if (!(actual is EnvDTE90a.StackFrame2 selected) || selected.Depth != depth)
                throw new McpToolException(ToolErrorCode.ExecutionError, "Frame selection was not confirmed. Read debugger_get_callstack before retrying.");
            return Result(new { confirmed = true, contextId, processId, threadId, frameIndex,
                actual.FunctionName, actual.Module, actual.Language });
        }

        [McpServerTool(Name = "debugger_add_breakpoint", Title = "debugger_add_breakpoint"), Description("Add a file/line breakpoint, optionally with a condition. Returns the debugger's resulting breakpoint state; binding depends on loaded symbols.")]
        public async Task<CallToolResult> AddAsync(string path, int line, string condition = "", CancellationToken cancellationToken = default)
        {
            if (!System.IO.Path.IsPathRooted(path) || line < 1) throw new ArgumentException("Absolute file path and positive line are required.");
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var language = string.Equals(System.IO.Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase) ? "C#" : "";
            var added = vs.Dte.Debugger.Breakpoints.Add(File: path, Line: line, Condition: condition, Language: language);
            return Result(new { count = added.Count });
        }
        [McpServerTool(Name = "debugger_list_breakpoints", Title = "debugger_list_breakpoints", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List top-level debugger breakpoints with location, condition and enabled state.")]
        public async Task<CallToolResult> ListAsync(CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(vs.Dte.Debugger.Breakpoints.Cast<Breakpoint>().Select(b => { ThreadHelper.ThrowIfNotOnUIThread(); return new { path = b.File, line = b.FileLine, column = b.FileColumn, b.FunctionName, b.Condition, b.Enabled }; }).ToArray());
        }
        [McpServerTool(Name = "breakpoint_enable", Title = "breakpoint_enable"), Description("Enable or disable all top-level breakpoints at an exact absolute file path and line. Fails if none match.")]
        public async Task<CallToolResult> EnableAsync(string path, int line, bool enabled, CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var matches = Matches(path, line); foreach (var b in matches) b.Enabled = enabled; return Result(new { count = matches.Length, enabled });
        }
        private Breakpoint[] Matches(string path, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!System.IO.Path.IsPathRooted(path) || line < 1) throw new ArgumentException("Absolute path and positive line are required.");
            var matches = vs.Dte.Debugger.Breakpoints.Cast<Breakpoint>().Where(b => { ThreadHelper.ThrowIfNotOnUIThread(); return VsContext.Same(b.File, path) && b.FileLine == line; }).ToArray();
            if (matches.Length == 0) throw new ArgumentException("No matching breakpoint."); return matches;
        }
        [McpServerTool(Name = "debugger_remove_breakpoint", Title = "debugger_remove_breakpoint", Destructive = true), Description("Delete all top-level breakpoints at an exact absolute file path and line. Fails if none match.")]
        public async Task<CallToolResult> RemoveAsync(string path, int line, CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var matches = Matches(path, line); foreach (var b in matches) b.Delete(); return Result(new { removed = matches.Length });
        }
        [McpServerTool(Name = "debugger_evaluate", Title = "debugger_evaluate", ReadOnly = false, Destructive = true), Description("Evaluate an expression in the paused debugger context. Evaluation can invoke code and mutate the debuggee. Timeout is passed to the debugger in milliseconds.")]
        public async Task<CallToolResult> EvaluateAsync(string expression, int timeoutMilliseconds = 1000, CancellationToken cancellationToken = default)
        {
            if (timeoutMilliseconds < 1 || timeoutMilliseconds > 10000) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); Paused();
            var value = vs.Dte.Debugger.GetExpression(expression, false, timeoutMilliseconds);
            return Result(new { value.Name, value.Type, value.Value, value.IsValidValue });
        }
        [McpServerTool(Name = "debugger_get_locals", Title = "debugger_get_locals", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Read bounded locals from the current paused stack frame. Values may be debugger-formatted.")]
        public async Task<CallToolResult> LocalsAsync(int maxResults = 100, CancellationToken cancellationToken = default)
        {
            if (maxResults < 1 || maxResults > 1000) throw new ArgumentOutOfRangeException(nameof(maxResults));
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); Paused();
            var locals = vs.Dte.Debugger.CurrentStackFrame.Locals;
            return Result(new { items = locals.Cast<Expression>().Take(maxResults).Select(e => { ThreadHelper.ThrowIfNotOnUIThread(); return new { e.Name, e.Type, e.Value, e.IsValidValue }; }).ToArray(), truncated = locals.Count > maxResults });
        }
        [McpServerTool(Name = "debugger_get_callstack", Title = "debugger_get_callstack", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Read bounded stack frames from the current thread of a paused debug session, with one-based frameIndex, isCurrent, contextId, processId and threadId for safe frame selection.")]
        public async Task<CallToolResult> StackAsync(int maxFrames = 100, CancellationToken cancellationToken = default)
        {
            if (maxFrames < 1 || maxFrames > 1000) throw new ArgumentOutOfRangeException(nameof(maxFrames));
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var contextId = ContextId();
            var debugger = vs.Dte.Debugger;
            var processId = debugger.CurrentProcess.ProcessID;
            var threadId = debugger.CurrentThread.ID;
            var frames = debugger.CurrentThread.StackFrames;
            var current = debugger.CurrentStackFrame as EnvDTE90a.StackFrame2 ?? throw new McpToolException(ToolErrorCode.NotSupported, "This debug engine does not expose stack frame depth.");
            var currentDepth = current.Depth;
            var items = frames.Cast<StackFrame>().Take(maxFrames).Select((f, index) => {
                ThreadHelper.ThrowIfNotOnUIThread();
                return new { frameIndex = index + 1, isCurrent = f is EnvDTE90a.StackFrame2 frame && frame.Depth == currentDepth, f.FunctionName, f.Module, f.Language };
            }).ToArray();
            RequireContext(contextId, processId, threadId);
            return Result(new { contextId, processId, threadId, frames = items, truncated = frames.Count > maxFrames });
        }
    }
}
