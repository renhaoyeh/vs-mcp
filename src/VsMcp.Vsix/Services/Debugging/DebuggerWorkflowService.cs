using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using EnvDTE90;
using EnvDTE90a;
using Microsoft.VisualStudio.Shell;
using VsMcp.Contracts;
using VsMcp.Mcp;
using VsMcp.Services.VisualStudio;

namespace VsMcp.Services.Debugging
{
    /// <summary>UI-thread operations for debugger workflow tools, kept in the owning provider instance.</summary>
    internal sealed class DebuggerWorkflowService
    {
        private readonly VsContext _vs;
        private readonly string _handlePrefix = "VsMcp.DebuggerWorkflow:" + Guid.NewGuid().ToString("N") + ":";

        public DebuggerWorkflowService(VsContext vs) { _vs = vs; }

        public DebuggerProcessListOutput Processes(int maxResults)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (maxResults < 1 || maxResults > 1000) throw new McpToolException(ToolErrorCode.InvalidArgument, "maxResults must be between 1 and 1000.");
            var debugger = _vs.Dte.Debugger;
            var debugged = debugger.DebuggedProcesses.Cast<EnvDTE.Process>().Take(maxResults + 1).ToArray();
            var local = debugger.LocalProcesses.Cast<EnvDTE.Process>().Take(maxResults + 1).ToArray();
            var activeId = debugger.CurrentProcess?.ProcessID;
            var byId = debugged.ToDictionary(p => p.ProcessID);
            return new DebuggerProcessListOutput
            {
                Instance = _vs.ReadInstance(),
                ContextId = debugger.CurrentMode == dbgDebugMode.dbgBreakMode ? _vs.Debug().BreakContext.Id : null,
                CurrentProcessId = activeId,
                Mode = debugger.CurrentMode.ToString(),
                LocalProcesses = local.Take(maxResults).Select(p => ProcessInfo(p, byId.ContainsKey(p.ProcessID), p.ProcessID == activeId)).ToList(),
                DebuggedProcesses = debugged.Take(maxResults).Select(p => ProcessInfo(p, true, p.ProcessID == activeId)).ToList(),
                Truncated = local.Length > maxResults || debugged.Length > maxResults,
            };
        }

        private static DebuggerProcessInfo ProcessInfo(EnvDTE.Process process, bool isDebugged, bool current)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new DebuggerProcessInfo { ProcessId = process.ProcessID, Name = process.Name, IsDebugged = isDebugged, IsCurrent = current };
            try
            {
                using (var osProcess = System.Diagnostics.Process.GetProcessById(process.ProcessID))
                    result.Path = osProcess.MainModule?.FileName;
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is System.ComponentModel.Win32Exception || error is NotSupportedException)
            {
                result.PathError = error.Message;
            }
            return result;
        }

        public object SelectProcess(string contextId, int processId, string expectedPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RequirePausedContext(contextId);
            if (processId <= 0 || string.IsNullOrWhiteSpace(expectedPath) || !System.IO.Path.IsPathRooted(expectedPath))
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Provide a positive debugged processId and its absolute expectedPath.");
            var matches = _vs.Dte.Debugger.DebuggedProcesses.Cast<EnvDTE.Process>().Where(p => p.ProcessID == processId).ToArray();
            if (matches.Length != 1) throw new McpToolException(ToolErrorCode.InvalidArgument, "processId must identify exactly one process already debugged by this Visual Studio instance.");
            var path = GetExactProcessPath(processId);
            if (!VsContext.Same(path, System.IO.Path.GetFullPath(expectedPath)))
                throw new McpToolException(ToolErrorCode.InvalidArgument, "The debugged PID executable does not match expectedPath.");

            _vs.Dte.Debugger.CurrentProcess = matches[0];
            _vs.Debug().BreakContext.Invalidate();
            var current = _vs.Dte.Debugger.CurrentProcess;
            if (current?.ProcessID != processId)
                throw new McpToolException(ToolErrorCode.ExecutionError, "Process selection was not confirmed. Read debugger_processes before retrying.");
            return new { confirmed = true, processId, path, contextId = _vs.Debug().BreakContext.Id, mode = _vs.Dte.Debugger.CurrentMode.ToString() };
        }

        public object Detach(int processId, string expectedPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (processId <= 0 || string.IsNullOrWhiteSpace(expectedPath) || !System.IO.Path.IsPathRooted(expectedPath))
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Provide a positive debugged processId and its absolute expectedPath.");
            EnsureNoBuildOrTestOrPending();
            if (_vs.Dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                throw new McpToolException(ToolErrorCode.ExecutionError, "Pause the debug session before detaching a selected process.");
            var path = GetExactProcessPath(processId);
            if (!VsContext.Same(path, System.IO.Path.GetFullPath(expectedPath)))
                throw new McpToolException(ToolErrorCode.InvalidArgument, "The PID executable does not match expectedPath.");
            var matches = _vs.Dte.Debugger.DebuggedProcesses.Cast<EnvDTE.Process>().Where(p => p.ProcessID == processId).ToArray();
            if (matches.Length != 1) throw new McpToolException(ToolErrorCode.InvalidArgument, "processId must identify exactly one currently debugged process.");
            // Only the selected exact debugged Process object is passed to DTE. Do not retry this mutation after transport interruption.
            matches[0].Detach(false);
            _vs.Debug().BreakContext.Invalidate();
            var stillAttached = _vs.Dte.Debugger.DebuggedProcesses.Cast<EnvDTE.Process>().Any(p => p.ProcessID == processId);
            if (stillAttached)
                return new { status = "pending", processId, path, debugged = true, preservesTargetProcess = true, message = "The detach request was issued but the debugger still lists this process. Inspect debugger_processes; do not repeat the mutation." };
            return new { status = "detached", processId, path, debugged = false, preservesTargetProcess = true, contextId = _vs.Debug().BreakContext.Id };
        }

        public DebuggerVariablesOutput Variables(string contextId, int processId, int threadId, int frameIndex, string expression, bool includeArguments, int maxDepth, int maxNodes, int timeoutMilliseconds)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (maxDepth < 0 || maxDepth > 8 || maxNodes < 1 || maxNodes > 500 || timeoutMilliseconds < 1 || timeoutMilliseconds > 10000)
                throw new McpToolException(ToolErrorCode.InvalidArgument, "maxDepth must be 0..8, maxNodes 1..500, and timeoutMilliseconds 1..10000.");
            if (string.IsNullOrWhiteSpace(expression) && !includeArguments)
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Provide an expression or set includeArguments=true.");
            if (frameIndex < 1 || frameIndex > 1000)
                throw new McpToolException(ToolErrorCode.InvalidArgument, "frameIndex must be between 1 and 1000.");
            var hasExpression = !string.IsNullOrWhiteSpace(expression);
            RequirePausedContext(contextId, processId, threadId, hasExpression ? frameIndex : null);
            var debugger = _vs.Dte.Debugger;
            var targetFrame = debugger.CurrentThread.StackFrames.Cast<StackFrame>().Skip(frameIndex - 1).FirstOrDefault();
            if (targetFrame == null)
                throw new McpToolException(ToolErrorCode.InvalidArgument, "frameIndex is not present in the current call stack; read debugger_get_callstack again.");
            var output = new DebuggerVariablesOutput { Instance = _vs.ReadInstance(), ContextId = contextId, ProcessId = processId, ThreadId = threadId, FrameIndex = frameIndex };
            var count = 0;
            if (hasExpression)
            {
                var root = debugger.GetExpression(expression, true, timeoutMilliseconds);
                output.Items.Add(ReadVariable(root, 0, maxDepth, maxNodes, ref count, output));
            }
            if (includeArguments)
            {
                var args = targetFrame.Arguments;
                foreach (Expression argument in args)
                {
                    if (count >= maxNodes) { output.Truncated = true; break; }
                    output.Items.Add(ReadVariable(argument, 0, maxDepth, maxNodes, ref count, output));
                }
            }
            RequirePausedContext(contextId, processId, threadId, hasExpression ? frameIndex : null);
            if (debugger.CurrentThread.StackFrames.Cast<StackFrame>().Skip(frameIndex - 1).FirstOrDefault() == null)
                throw new McpToolException(ToolErrorCode.InvalidArgument, "frameIndex is no longer present in the current call stack; read debugger_get_callstack again.");
            return output;
        }

        private DebuggerVariableNode ReadVariable(Expression expression, int depth, int maxDepth, int maxNodes, ref int count, DebuggerVariablesOutput output)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            count++;
            var node = new DebuggerVariableNode { Name = expression.Name, Type = expression.Type, Value = expression.Value, IsValidValue = expression.IsValidValue };
            var children = expression.DataMembers;
            node.HasChildren = children.Count > 0;
            if (depth >= maxDepth || count >= maxNodes) { output.Truncated |= node.HasChildren && (depth < maxDepth || count >= maxNodes); return node; }
            foreach (Expression child in children)
            {
                if (count >= maxNodes) { output.Truncated = true; break; }
                node.Children.Add(ReadVariable(child, depth + 1, maxDepth, maxNodes, ref count, output));
            }
            return node;
        }

        public DebuggerBreakpointListOutput ListBreakpoints(int maxResults)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (maxResults < 1 || maxResults > 500) throw new McpToolException(ToolErrorCode.InvalidArgument, "maxResults must be between 1 and 500.");
            var breaks = _vs.Dte.Debugger.Breakpoints;
            var items = new List<DebuggerBreakpointInfo>();
            var truncated = false;
            foreach (Breakpoint breakpoint in breaks)
            {
                if (items.Count >= maxResults) { truncated = true; break; }
                items.Add(BreakpointInfo(breakpoint));
            }
            return new DebuggerBreakpointListOutput { Instance = _vs.ReadInstance(), Items = items, Truncated = truncated };
        }

        private DebuggerBreakpointInfo BreakpointInfo(Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var bp2 = breakpoint as Breakpoint2;
            var bp3 = breakpoint as Breakpoint3;
            var item = new DebuggerBreakpointInfo();
            item.Handle = Read(item, "handle", () => ReadHandle(breakpoint), null);
            item.LocationType = Read(item, "locationType", () => bp2?.LocationType.ToString() ?? "unknown", "unknown");
            item.File = Read(item, "file", () => breakpoint.File, null);
            var line = Read(item, "line", () => breakpoint.FileLine, 0);
            item.Line = line > 0 ? (int?)line : null;
            var column = Read(item, "column", () => breakpoint.FileColumn, 0);
            item.Column = column > 0 ? (int?)column : null;
            item.FunctionName = Read(item, "functionName", () => breakpoint.FunctionName, null);
            item.Language = Read(item, "language", () => breakpoint.Language, null);
            item.Condition = Read(item, "condition", () => breakpoint.Condition, null);
            item.ConditionType = Read(item, "conditionType", () => breakpoint.ConditionType.ToString(), "unknown");
            item.HitCountTarget = Read(item, "hitCountTarget", () => bp2?.HitCountTarget ?? 0, 0);
            item.HitCountType = Read(item, "hitCountType", () => bp2?.HitCountType.ToString() ?? "unknown", "unknown");
            item.CurrentHits = Read(item, "currentHits", () => breakpoint.CurrentHits, 0);
            item.Enabled = Read(item, "enabled", () => breakpoint.Enabled, false);
            item.Binding = Read(item, "binding", () => breakpoint.Type.ToString(), "unknown");
            item.BoundAddressOrError = Read(item, "boundAddressOrError", () => bp3?.Address, null);
            item.BoundChildrenCount = Read<int?>(item, "boundChildrenCount", () => breakpoint.Children?.Count, null);
            return item;
        }

        private static T Read<T>(DebuggerBreakpointInfo item, string field, Func<T> read, T fallback)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return read(); }
            catch (Exception error) { item.ReadErrors.Add(field + ": " + ErrorText(error)); return fallback; }
        }

        private string ReadHandle(Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var tag = breakpoint.Tag;
            return !string.IsNullOrEmpty(tag) && tag.StartsWith(_handlePrefix, StringComparison.Ordinal) ? tag.Substring(_handlePrefix.Length) : null;
        }

        public object AddBreakpoint(string kind, string functionName, string path, int line, int column, string language, string dataExpression, int dataCount, string address, string condition, string conditionType, int hitCount, string hitCountType)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureBreakpointMutationAllowed();
            var function = kind == "function" ? functionName : "";
            var file = kind == "file" ? path : "";
            var data = kind == "data" ? dataExpression : "";
            var addr = kind == "address" ? address : "";
            if (kind == "function" && string.IsNullOrWhiteSpace(function) || kind == "file" && (!System.IO.Path.IsPathRooted(path) || line < 1) || kind == "data" && string.IsNullOrWhiteSpace(data) || kind == "address" && string.IsNullOrWhiteSpace(addr) || !new[] { "file", "function", "data", "address" }.Contains(kind))
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Specify exactly one valid location: file, function, data or address.");
            if (dataCount < 1 || dataCount > 100 || hitCount < 0) throw new McpToolException(ToolErrorCode.InvalidArgument, "dataCount must be 1..100 and hitCount must be nonnegative.");
            if (conditionType != "whenTrue" && conditionType != "changed") throw new McpToolException(ToolErrorCode.InvalidArgument, "conditionType must be whenTrue or changed.");
            if (hitCountType != "none" && hitCountType != "equalTo" && hitCountType != "greaterThanOrEqualTo" && hitCountType != "multipleOf")
                throw new McpToolException(ToolErrorCode.InvalidArgument, "hitCountType must be none, equalTo, greaterThanOrEqualTo or multipleOf.");
            if ((hitCountType == "none" && hitCount != 0) || (hitCountType != "none" && hitCount < 1))
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Use hitCount=0 with none, or a positive hitCount with a hit-count policy.");
            var conditionEnum = conditionType == "changed" ? dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenChanged : dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue;
            var hitEnum = ParseHitCountType(hitCountType);
            var allBreakpoints = _vs.Dte.Debugger.Breakpoints;
            Breakpoints addResult;
            try
            {
                addResult = allBreakpoints.Add(Function: function, File: file, Line: line, Column: column, Condition: condition ?? "", ConditionType: conditionEnum,
                    Language: language ?? "", Data: data, DataCount: dataCount, Address: addr, HitCount: hitCount, HitCountType: hitEnum);
            }
            catch (Exception error)
            {
                throw ApiFailure("adding the " + kind + " breakpoint (the mutation may have applied; inspect debugger_breakpoint_list before retrying)", error);
            }

            if (addResult == null || addResult.Count < 1)
                throw new McpToolException(ToolErrorCode.ExecutionError, "Visual Studio may have added the breakpoint but returned no breakpoint object. Inspect debugger_breakpoint_list before retrying.");
            var created = addResult.Item(1);
            var handle = Guid.NewGuid().ToString("N");
            var tag = _handlePrefix + handle;
            try
            {
                if (!string.IsNullOrEmpty(created.Tag))
                    throw new InvalidOperationException("The returned breakpoint already has a user tag; refusing to overwrite it.");
                if (_vs.Dte.Debugger.Breakpoints.Cast<Breakpoint>().Any(b => string.Equals(b.Tag, tag, StringComparison.Ordinal)))
                    throw new InvalidOperationException("The generated breakpoint handle tag already exists.");
                created.Tag = tag;
                if (!string.Equals(created.Tag, tag, StringComparison.Ordinal))
                    throw new InvalidOperationException("Visual Studio did not preserve the generated breakpoint tag.");
            }
            catch (Exception error)
            {
                throw ApiFailure("assigning the provider handle to the new breakpoint (the breakpoint may exist without a handle; inspect debugger_breakpoint_list before retrying)", error);
            }
            DebuggerBreakpointInfo readback = null;
            string readbackError = null;
            try { readback = BreakpointInfo(created); }
            catch (Exception error) { readbackError = ErrorText(error); }
            return new { handle, requestedKind = kind, breakpoint = readback, readbackError, readbackConfirmed = readback != null && readback.Enabled && (condition ?? "") == readback.Condition };
        }

        public object UpdateBreakpoint(string handle, bool enabled)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); EnsureBreakpointMutationAllowed();
            var breakpoint = FindBreakpoint(handle);
            try { breakpoint.Enabled = enabled; }
            catch (Exception error) { throw ApiFailure("updating breakpoint " + handle + " (read its current state before retrying)", error); }
            var state = BreakpointInfo(breakpoint);
            return new { handle, breakpoint = state, readbackConfirmed = state.Enabled == enabled };
        }

        public object RemoveBreakpoint(string handle)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); EnsureBreakpointMutationAllowed();
            var breakpoint = FindBreakpoint(handle);
            var snapshot = BreakpointInfo(breakpoint);
            try { breakpoint.Delete(); }
            catch (Exception error) { throw ApiFailure("deleting breakpoint " + handle + " (inspect debugger_breakpoint_list before retrying)", error); }
            bool remains;
            var tag = _handlePrefix + handle;
            try { remains = _vs.Dte.Debugger.Breakpoints.Cast<Breakpoint>().Any(b => string.Equals(b.Tag, tag, StringComparison.Ordinal)); }
            catch (Exception error) { throw ApiFailure("reading back deleted breakpoint " + handle + " (do not repeat the mutation yet)", error); }
            return new { handle, removed = !remains, previous = snapshot };
        }

        private Breakpoint FindBreakpoint(string handle)
        {
            if (string.IsNullOrWhiteSpace(handle)) throw new McpToolException(ToolErrorCode.InvalidArgument, "A handle returned by debugger_breakpoint_add is required.");
            var tag = _handlePrefix + handle;
            var matches = _vs.Dte.Debugger.Breakpoints.Cast<Breakpoint>().Where(b => string.Equals(b.Tag, tag, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new McpToolException(ToolErrorCode.InvalidArgument, "The breakpoint handle is unknown or no longer present exactly once; read debugger_breakpoint_list.");
            return matches[0];
        }

        private static dbgHitCountType ParseHitCountType(string type) => type switch
        {
            "equalTo" => dbgHitCountType.dbgHitCountTypeEqual,
            "greaterThanOrEqualTo" => dbgHitCountType.dbgHitCountTypeGreaterOrEqual,
            "multipleOf" => dbgHitCountType.dbgHitCountTypeMultiple,
            _ => dbgHitCountType.dbgHitCountTypeNone,
        };

        public object GetException(string groupName, string exceptionName, uint? code, int maxResults)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (maxResults < 1 || maxResults > 500) throw new McpToolException(ToolErrorCode.InvalidArgument, "maxResults must be between 1 and 500.");
            var hasGroup = !string.IsNullOrWhiteSpace(groupName);
            var hasName = !string.IsNullOrWhiteSpace(exceptionName);
            if (!hasGroup)
            {
                if (hasName || code.HasValue) throw new McpToolException(ToolErrorCode.InvalidArgument, "Provide groupName before selecting an exception name or code.");
                return ListExceptionGroups(maxResults);
            }
            if (!hasName && !code.HasValue) return ListExceptionSettings(groupName, maxResults);
            if (hasName == code.HasValue) throw new McpToolException(ToolErrorCode.InvalidArgument, "Provide exactly one of exceptionName or code.");
            return ReadException(groupName, exceptionName, code, null, null);
        }

        private object ListExceptionGroups(int maxResults)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var debugger = _vs.Dte.Debugger as Debugger4;
                if (debugger == null) throw new McpToolException(ToolErrorCode.NotSupported, "This Visual Studio debugger does not expose Debugger4.ExceptionGroups.");
                var groups = debugger.ExceptionGroups;
                var count = groups.Count;
                var items = new List<DebuggerExceptionGroupInfo>();
                var limit = Math.Min(count, maxResults);
                for (var index = 1; index <= limit; index++)
                {
                    var settings = groups.Item(index);
                    items.Add(new DebuggerExceptionGroupInfo
                    {
                        Name = settings.Name,
                        SupportsExceptionCodes = settings.SupportsExceptionCodes,
                        SettingCount = settings.Count,
                    });
                }
                return new { groups = items, truncated = count > maxResults, totalCount = count };
            }
            catch (McpToolException) { throw; }
            catch (Exception error) { throw ApiFailure("listing exception groups by indexed COM access", error); }
        }

        private object ListExceptionSettings(string groupName, int maxResults)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var debugger = _vs.Dte.Debugger as Debugger4;
                if (debugger == null) throw new McpToolException(ToolErrorCode.NotSupported, "This Visual Studio debugger does not expose Debugger4.ExceptionGroups.");
                var settings = debugger.ExceptionGroups.Item(groupName);
                if (settings == null || !string.Equals(settings.Name, groupName, StringComparison.Ordinal))
                    throw new McpToolException(ToolErrorCode.InvalidArgument, "No exception group exactly matches groupName.");
                var count = settings.Count;
                var supportsCodes = settings.SupportsExceptionCodes;
                var limit = Math.Min(count, maxResults);
                var items = new List<DebuggerExceptionSettingInfo>();
                for (var index = 1; index <= limit; index++)
                {
                    var setting = settings.Item(index);
                    items.Add(new DebuggerExceptionSettingInfo
                    {
                        Name = setting.Name,
                        Code = supportsCodes ? (uint?)setting.Code : null,
                        BreakWhenThrown = setting.BreakWhenThrown,
                        BreakWhenUserUnhandled = setting.BreakWhenUserUnhandled,
                    });
                }
                return new { group = groupName, supportsExceptionCodes = supportsCodes, items, truncated = count > maxResults, totalCount = count };
            }
            catch (McpToolException) { throw; }
            catch (Exception error) { throw ApiFailure("listing exception settings in group '" + groupName + "' by indexed COM access", error); }
        }

        public object SetException(string groupName, string exceptionName, uint? code, bool? breakWhenThrown, bool? breakWhenUserUnhandled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!breakWhenThrown.HasValue && !breakWhenUserUnhandled.HasValue) throw new McpToolException(ToolErrorCode.InvalidArgument, "Provide breakWhenThrown and/or breakWhenUserUnhandled.");
            var previous = ReadException(groupName, exceptionName, code, null, null);
            var readback = ReadException(groupName, exceptionName, code, breakWhenThrown, breakWhenUserUnhandled);
            return new { previous, readback, confirmed = (!breakWhenThrown.HasValue || readback.BreakWhenThrown == breakWhenThrown.Value) && (!breakWhenUserUnhandled.HasValue || readback.BreakWhenUserUnhandled == breakWhenUserUnhandled.Value) };
        }

        private DebuggerExceptionInfo ReadException(string groupName, string exceptionName, uint? code, bool? breakWhenThrown, bool? breakWhenUserUnhandled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(groupName) || (code.HasValue == !string.IsNullOrWhiteSpace(exceptionName)))
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Provide an exact groupName and exactly one of exceptionName or code.");
            var mutating = breakWhenThrown.HasValue || breakWhenUserUnhandled.HasValue;
            if (mutating)
            {
                EnsureNoBuildOrTestOrPending();
                if (_vs.Dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode) throw new McpToolException(ToolErrorCode.ExecutionError, "Exception policy changes require an idle debugger.");
            }
            try
            {
                var debugger = _vs.Dte.Debugger as Debugger4;
                if (debugger == null) throw new McpToolException(ToolErrorCode.NotSupported, "This Visual Studio debugger does not expose Debugger4.ExceptionGroups.");
                // The COM collection's IEnumerable implementation is broken in some debugger builds; use its indexed API.
                var settings = debugger.ExceptionGroups.Item(groupName);
                if (settings == null || !string.Equals(settings.Name, groupName, StringComparison.Ordinal)) throw new McpToolException(ToolErrorCode.InvalidArgument, "No exception group exactly matches groupName.");
                var supportsCodes = settings.SupportsExceptionCodes;
                if (code.HasValue && !supportsCodes) throw new McpToolException(ToolErrorCode.NotSupported, "This exception group does not support numeric exception codes.");
                var setting = code.HasValue ? settings.ItemFromCode(code.Value) : settings.Item(exceptionName);
                if (setting == null || (!code.HasValue && !string.Equals(setting.Name, exceptionName, StringComparison.Ordinal)))
                    throw new McpToolException(ToolErrorCode.InvalidArgument, "The exact exception name/code was not found.");
                if (breakWhenThrown.HasValue) settings.SetBreakWhenThrown(breakWhenThrown.Value, setting);
                if (breakWhenUserUnhandled.HasValue) settings.SetBreakWhenUserUnhandled(breakWhenUserUnhandled.Value, setting);
                // ExceptionSetting is a snapshot; reacquire from the collection after setting via its supported setter methods.
                var readback = code.HasValue ? settings.ItemFromCode(code.Value) : settings.Item(exceptionName);
                return new DebuggerExceptionInfo { Group = settings.Name, Name = readback.Name, Code = supportsCodes ? (uint?)readback.Code : null, BreakWhenThrown = readback.BreakWhenThrown, BreakWhenUserUnhandled = readback.BreakWhenUserUnhandled };
            }
            catch (McpToolException) { throw; }
            catch (Exception error)
            {
                var target = code.HasValue ? "code 0x" + code.Value.ToString("X8") : "'" + exceptionName + "'";
                throw ApiFailure("reading or setting exception policy for " + target + " in group '" + groupName + "'", error);
            }
        }

        private void EnsureBreakpointMutationAllowed()
        {
            EnsureNoBuildOrTestOrPending();
            if (_vs.Dte.Debugger.CurrentMode == dbgDebugMode.dbgRunMode) throw new McpToolException(ToolErrorCode.ExecutionError, "Breakpoint mutations require an idle or paused debugger.");
        }

        private void EnsureNoBuildOrTestOrPending()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_vs.DebugLaunchPending || _vs.TestsRunning() || _vs.Dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
                throw new McpToolException(ToolErrorCode.ExecutionError, "Wait for builds, tests and pending launches to finish before using this debugger workflow operation.");
        }

        private void RequirePausedContext(string contextId, int? processId = null, int? threadId = null, int? frameIndex = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureNoBuildOrTestOrPending();
            var debugger = _vs.Dte.Debugger;
            if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode) throw new McpToolException(ToolErrorCode.ExecutionError, "Debugger must be paused.");
            try { _vs.Debug().BreakContext.Require(contextId); }
            catch (ArgumentException error) { throw new McpToolException(ToolErrorCode.InvalidArgument, error.Message); }
            if (processId.HasValue && debugger.CurrentProcess?.ProcessID != processId.Value)
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Current debug process changed; read debugger_processes again.");
            if (threadId.HasValue && debugger.CurrentThread?.ID != threadId.Value)
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Current debug thread changed; read debugger_get_callstack again.");
            if (frameIndex.HasValue)
            {
                var frames = debugger.CurrentThread.StackFrames;
                var expected = frames.Cast<StackFrame>().Skip(frameIndex.Value - 1).FirstOrDefault() as EnvDTE90a.StackFrame2;
                var selected = debugger.CurrentStackFrame as EnvDTE90a.StackFrame2;
                if (expected == null)
                    throw new McpToolException(ToolErrorCode.InvalidArgument, "frameIndex is not present in the current call stack; read debugger_get_callstack again.");
                if (selected == null)
                    throw new McpToolException(ToolErrorCode.NotSupported, "The debug engine does not expose stack-frame depth required to validate the selected frame.");
                if (selected.Depth != expected.Depth)
                    throw new McpToolException(ToolErrorCode.InvalidArgument, "Current stack frame changed; read debugger_get_callstack again.");
            }
        }

        private static McpToolException ApiFailure(string operation, Exception error) =>
            new McpToolException(ToolErrorCode.ExecutionError, "Visual Studio debugger API failed while " + operation + ": " + ErrorText(error));

        private static string ErrorText(Exception error)
        {
            var detail = error is COMException com ? "COM 0x" + com.ErrorCode.ToString("X8") + ": " : error.GetType().FullName + ": ";
            return detail + error.Message;
        }

        private static string GetExactProcessPath(int processId)
        {
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(processId))
                    return process.MainModule?.FileName ?? throw new McpToolException(ToolErrorCode.NotSupported, "The OS did not expose the process image path.");
            }
            catch (ArgumentException) { throw new McpToolException(ToolErrorCode.InvalidArgument, "The process no longer exists."); }
            catch (System.ComponentModel.Win32Exception error) { throw new McpToolException(ToolErrorCode.NotSupported, "Cannot verify the process image path: " + error.Message); }
        }
    }
}
