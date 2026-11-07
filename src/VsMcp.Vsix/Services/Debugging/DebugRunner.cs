using VsMcp.Services.Build;
using VsMcp.Services.Debugging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;
using VsMcp.Mcp;

namespace VsMcp.Services.Debugging
{
    internal sealed class DebugRunner
    {
        private readonly DTE2 _dte;
        private readonly DebuggerEvents _events;
        private Launch _last;
        private readonly JoinableTaskFactory _factory;
        private Launch _lastStop;
        private int _sessionGeneration;

        private static readonly JsonSerializer ModeSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
        });

        private static readonly JsonSerializer PhaseSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
        });

        private enum DebugPhase
        {
            [EnumMember(Value = "requested")]
            Requested,
            [EnumMember(Value = "running")]
            Running,
            [EnumMember(Value = "break")]
            Break,
            [EnumMember(Value = "ended")]
            Ended,
            [EnumMember(Value = "failed")]
            Failed,
            [EnumMember(Value = "stopped")]
            Stopped,
            [EnumMember(Value = "already_stopped")]
            AlreadyStopped,
        }

        private sealed class Launch
        {
            public DebugPhase Phase;
            public string Error;
        }

        public DebugRunner(DTE2 dte, JoinableTaskFactory factory)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte = dte;
            _factory = factory;
            // Retain the COM event source for the lifetime of the provider.
            _events = dte.Events.DebuggerEvents;
            _events.OnEnterRunMode += reason => Observe(DebugPhase.Running);
            _events.OnEnterBreakMode += OnBreak;
            _events.OnEnterDesignMode += reason => Observe(DebugPhase.Ended);
        }

        private void OnBreak(dbgEventReason reason, ref dbgExecutionAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Observe(DebugPhase.Break);
        }
        private void Observe(DebugPhase phase)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_last != null && _last.Phase != DebugPhase.Failed && _last.Phase != DebugPhase.Ended) _last.Phase = phase;
            if (phase == DebugPhase.Ended)
            {
                _sessionGeneration++;
                if (_lastStop?.Phase == DebugPhase.Requested) _lastStop.Phase = DebugPhase.Stopped;
            }
            Notify();
        }

        // Completed on every debugger event or request failure so waiting status calls wake immediately.
        private TaskCompletionSource<bool> _signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Notify()
        {
            var previous = _signal;
            _signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult(true);
        }

        /// <summary>Status, optionally waiting until no launch or stop request is unresolved (or the wait elapses).</summary>
        public async Task<JObject> StatusAsync(JObject instance, TimeSpan wait, System.Threading.CancellationToken cancellationToken)
        {
            await _factory.SwitchToMainThreadAsync(cancellationToken);
            var deadline = DateTime.UtcNow + wait;
            while (true)
            {
                var result = Status(instance);
                var remaining = deadline - DateTime.UtcNow;
                if (!IsPending || remaining <= TimeSpan.Zero) return result;
                // Cap each slice so a missed event (e.g. process exit without a mode change) is still rechecked.
                var slice = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
                await Task.WhenAny(_signal.Task, Task.Delay(slice, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                await _factory.SwitchToMainThreadAsync(cancellationToken);
            }
        }

        public bool IsPending => _last?.Phase == DebugPhase.Requested || _lastStop?.Phase == DebugPhase.Requested;

        private string[] StartupProjects()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return (_dte.Solution.SolutionBuild.StartupProjects as Array)?
                .Cast<object>().Select(p => Convert.ToString(p)).ToArray() ?? Array.Empty<string>();
        }

        public JObject ShowWindow(JObject instance)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte.MainWindow.Visible = true;
            _dte.MainWindow.Activate();
            return new JObject
            {
                ["instance"] = instance,
                ["visible"] = _dte.MainWindow.Visible,
                ["title"] = _dte.MainWindow.Caption,
            };
        }
        public JObject Start(JObject instance)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_dte.Solution.IsOpen)
                throw new McpToolException(ToolErrorCode.NoSolution, "No solution is open.");
            if (IsPending)
                throw new McpToolException(ToolErrorCode.DebugLaunchPending, "A launch is unresolved. Inspect get_debug_status and VS before retrying.");
            if (_dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
                throw new McpToolException(ToolErrorCode.DebuggerActive, "The debugger is already active; this tool never resumes or restarts it.");
            if (BuildRunner.IsRunning || BuildRunner.IsBuildManagerBusy())
                throw new McpToolException(ToolErrorCode.BuildInProgress, "Wait for the current build to finish before starting the debugger.");
            var startup = StartupProjects();
            if (startup.Length == 0)
                throw new McpToolException(ToolErrorCode.NoStartupProject, "The solution has no startup project.", Status(instance));
            var active = _dte.Solution.SolutionBuild.ActiveConfiguration as SolutionConfiguration2;
            ShowWindow(instance);
            if (!_dte.Commands.Item("Debug.Start", 0).IsAvailable)
                throw new McpToolException(ToolErrorCode.DebugStartUnavailable, "VS currently disables Debug.Start.");

            _last = new Launch { Phase = DebugPhase.Requested };
            // Use VS's asynchronous command queue rather than invoking Go inside this MCP call.
            var group = VSConstants.GUID_VSStandardCommandSet97;
            object input = null;
            try
            {
                var shell = (IVsUIShell)Package.GetGlobalService(typeof(SVsUIShell));
                ErrorHandler.ThrowOnFailure(shell.PostExecCommand(ref group, (uint)VSConstants.VSStd97CmdID.Start, 0, ref input));
            }
            catch (Exception ex)
            {
                _last.Phase = DebugPhase.Failed;
                _last.Error = ex.Message;
                throw new McpToolException(ToolErrorCode.DebugStartFailed, ex.Message, Status(instance));
            }
            // No debugger enumeration here: acceptance must not wait for launch or symbol loading.
            return new JObject { ["instance"] = instance, ["request"] = Describe(_last),
                ["startup_projects"] = new JArray(startup), ["solution_configuration"] = active?.Name,
                ["platform"] = active?.PlatformName, ["build_behavior"] = "VS F5 settings; dependencies may be built" };
        }

        private int[] ProcessIds()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var ids = new List<int>();
            foreach (EnvDTE.Process process in _dte.Debugger.DebuggedProcesses) ids.Add(process.ProcessID);
            return ids.OrderBy(p => p).ToArray();
        }

        public JObject Stop(JObject instance)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (IsPending)
                throw new McpToolException(ToolErrorCode.DebugOperationPending, "A debugger operation is unresolved; inspect get_debug_status first.");
            // The session this call sees is the one it stops: a queued stop never applies to a later session.
            var expected = ProcessIds();
            var idle = _dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode && expected.Length == 0;
            if (!idle && !_dte.Commands.Item("Debug.StopDebugging", 0).IsAvailable)
                throw new McpToolException(ToolErrorCode.DebugStopUnavailable, "VS currently disables Debug.StopDebugging.");
            var operation = new Launch { Phase = idle ? DebugPhase.AlreadyStopped : DebugPhase.Requested };
            _lastStop = operation;
            if (!idle)
            {
                var generation = _sessionGeneration;
                // Yield off the request before returning to the UI thread, then recheck that the session is unchanged.
                _ = _factory.RunAsync(async () =>
                {
                    await TaskScheduler.Default;
                    await _factory.SwitchToMainThreadAsync();
                    if (operation.Phase != DebugPhase.Requested) return;
                    try
                    {
                        var processes = ProcessIds();
                        if (_dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode && processes.Length == 0)
                            operation.Phase = DebugPhase.AlreadyStopped;
                        else if (_sessionGeneration != generation || !processes.SequenceEqual(expected))
                        {
                            operation.Phase = DebugPhase.Failed;
                            operation.Error = "Debug target changed before stop execution; stop was not issued.";
                        }
                        else
                            _dte.Debugger.Stop(false);
                    }
                    catch (Exception ex)
                    {
                        operation.Phase = DebugPhase.Failed;
                        operation.Error = ex.Message;
                    }
                    Notify();
                });
            }
            return new JObject { ["instance"] = instance, ["stop_request"] = Describe(operation) };
        }

        private static JObject Describe(Launch launch) => launch == null ? null : new JObject
        {
            ["phase"] = JToken.FromObject(launch.Phase, PhaseSerializer), ["error"] = launch.Error,
        };

        public JObject Status(JObject instance)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var mode = _dte.Debugger.CurrentMode;
            if (mode == dbgDebugMode.dbgRunMode) Observe(DebugPhase.Running);
            else if (mode == dbgDebugMode.dbgBreakMode) Observe(DebugPhase.Break);
            var active = _dte.Solution.IsOpen ? _dte.Solution.SolutionBuild.ActiveConfiguration as SolutionConfiguration2 : null;
            var processes = new JArray();
            foreach (EnvDTE.Process process in _dte.Debugger.DebuggedProcesses)
                processes.Add(new JObject { ["pid"] = process.ProcessID, ["path"] = process.Name });
            if (_lastStop?.Phase == DebugPhase.Requested && mode == dbgDebugMode.dbgDesignMode && processes.Count == 0)
                _lastStop.Phase = DebugPhase.Stopped;
            return new JObject
            {
                ["instance"] = instance, ["mode"] = JToken.FromObject(mode, ModeSerializer), ["request"] = Describe(_last),
                ["solution_configuration"] = active?.Name, ["platform"] = active?.PlatformName,
                ["startup_projects"] = _dte.Solution.IsOpen ? new JArray(StartupProjects()) : new JArray(),
                ["processes"] = processes,
                ["stop_request"] = Describe(_lastStop),
            };
        }
    }
}
