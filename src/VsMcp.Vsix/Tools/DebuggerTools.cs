using VsMcp.Services.VisualStudio;
using System;
using System.ComponentModel;
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
        public DebuggerTools(VsContext context) { vs = context; }
        private CallToolResult Result(object data) { ThreadHelper.ThrowIfNotOnUIThread(); return ToolJson.Result(new JObject { ["instance"] = vs.Instance(), ["data"] = JToken.Parse(System.Text.Json.JsonSerializer.Serialize(data, ToolJson.Options)) }); }
        private void Paused() {
            ThreadHelper.ThrowIfNotOnUIThread(); if (vs.Dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode) throw new InvalidOperationException("Debugger must be paused at a breakpoint."); }
        private async Task<CallToolResult> CommandAsync(string command, bool paused, CancellationToken token)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (paused) Paused(); else if (vs.Dte.Debugger.CurrentMode != dbgDebugMode.dbgRunMode) throw new InvalidOperationException("Debugger must be running.");
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
        [McpServerTool(Name = "debugger_get_callstack", Title = "debugger_get_callstack", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Read bounded stack frames from the current thread of a paused debug session.")]
        public async Task<CallToolResult> StackAsync(int maxFrames = 100, CancellationToken cancellationToken = default)
        {
            if (maxFrames < 1 || maxFrames > 1000) throw new ArgumentOutOfRangeException(nameof(maxFrames));
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); Paused();
            var frames = vs.Dte.Debugger.CurrentThread.StackFrames;
            return Result(new { frames = frames.Cast<StackFrame>().Take(maxFrames).Select(f => { ThreadHelper.ThrowIfNotOnUIThread(); return new { f.FunctionName, f.Module, f.Language }; }).ToArray(), truncated = frames.Count > maxFrames });
        }
    }
}
