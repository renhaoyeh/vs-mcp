using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using VsMcp.Mcp;
using VsMcp.Services.Build;
using VsMcp.Services.VisualStudio;

namespace VsMcp.Services.Debugging
{
    // The request survives cancellation/disconnection: a posted command must never be retried automatically.
    internal sealed class WithoutDebuggingRunner
    {
        private readonly VsContext _vs;
        private Task _observation;
        private JObject _status;
        private HashSet<string> _expected;
        private DateTime _requested;
        private LaunchTracking _tracking;
        public bool IsPending => _tracking?.IsPending == true;

        public WithoutDebuggingRunner(VsContext vs) { _vs = vs; }

        public JObject Status
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_status == null) return null;
                var result = (JObject)_status.DeepClone();
                result["status"] = _tracking.Status;
                return result;
            }
        }

        public async Task<JObject> StartAsync(int waitSeconds, CancellationToken cancellationToken)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var dte = _vs.Dte;
            if (!dte.Solution.IsOpen) throw new McpToolException(ToolErrorCode.NoSolution, "No solution is open.");
            if (_vs.DebugLaunchPending) throw new McpToolException(ToolErrorCode.DebugLaunchPending, "A launch is unresolved. Inspect get_debug_status before retrying.");
            if (dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode) throw new McpToolException(ToolErrorCode.DebuggerActive, "The debugger is active.");
            if (BuildRunner.IsRunning || BuildRunner.IsBuildManagerBusy()) throw new McpToolException(ToolErrorCode.BuildInProgress, "Wait for the current build.");
            var startup = (dte.Solution.SolutionBuild.StartupProjects as Array)?.Cast<object>().Select(Convert.ToString).ToArray() ?? Array.Empty<string>();
            if (startup.Length == 0) throw new McpToolException(ToolErrorCode.NoStartupProject, "The solution has no startup project.");
            _expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in startup)
            {
                var project = dte.Solution.Item(name);
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var configuration = project.ConfigurationManager.ActiveConfiguration;
                    var outputs = configuration.OutputGroups.Item("Built").FileURLs as Array;
                    if (outputs != null) foreach (var output in outputs)
                    {
                        var value = Convert.ToString(output);
                        var path = Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : Path.GetFullPath(value);
                        if (string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            // SDK-style Built output groups can point to obj; the apphost runs from OutputPath.
                            var outputPath = Convert.ToString(configuration.Properties.Item("OutputPath").Value);
                            if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("The active configuration has no OutputPath.");
                            path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project.FullName), outputPath, Path.ChangeExtension(Path.GetFileName(path), ".exe")));
                        }
                        if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
                    }
                }
                catch (Exception ex) { throw new McpToolException(ToolErrorCode.NotSupported, "Cannot resolve startup executable: " + ex.Message); }
                if (paths.Count != 1) throw new McpToolException(ToolErrorCode.NotSupported, "Startup project must expose exactly one executable in its active Built output group. External programs, browser launches and dotnet-hosted DLLs are not supported.");
                if (paths.Any(path => _expected.Contains(path))) throw new McpToolException(ToolErrorCode.NotSupported, "Startup projects with the same executable cannot be confirmed independently.");
                _expected.UnionWith(paths);
            }
            if (!dte.Commands.Item("Debug.StartWithoutDebugging", 0).IsAvailable) throw new McpToolException(ToolErrorCode.DebugStartUnavailable, "VS disables Debug.StartWithoutDebugging.");
            var active = (SolutionConfiguration2)dte.Solution.SolutionBuild.ActiveConfiguration;
            _requested = DateTime.UtcNow;
            var tracking = _tracking = new LaunchTracking();
            var status = _status = new JObject { ["instance"] = _vs.Instance(), ["request_id"] = tracking.Id,
                ["startup_projects"] = new JArray(startup), ["solution_configuration"] = active.Name,
                ["platform"] = active.PlatformName, ["expected_paths"] = new JArray(_expected),
                ["processes"] = new JArray(), ["requested_at"] = _requested.ToString("O"),
                ["observation_active"] = false,
                ["message"] = "Command acceptance is not success. Inspect get_debug_status or without_debugging_reconcile; never blindly retry." };
            try
            {
                var group = VSConstants.GUID_VSStandardCommandSet97;
                object input = null;
                ErrorHandler.ThrowOnFailure(((IVsUIShell)Package.GetGlobalService(typeof(SVsUIShell))).PostExecCommand(ref group, (uint)VSConstants.VSStd97CmdID.StartNoDebug, 0, ref input));
            }
            catch (Exception ex)
            {
                tracking.Fail();
                status["message"] = ex.Message;
                throw new McpToolException(ToolErrorCode.DebugStartFailed, ex.Message, Status);
            }
            // Immutable request inputs prevent a late observer from inspecting or updating a newer request.
            status["observation_active"] = true;
            var observation = _observation = ObserveAsync(tracking, status, _expected, _requested);
            await Task.WhenAny(observation, Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (!ReferenceEquals(_tracking, tracking))
                throw new McpToolException(ToolErrorCode.ExecutionError, "Launch tracking changed; inspect get_debug_status.");
            return Status;
        }

        public async Task<JObject> ReconcileAsync(string requestId, bool abandonTracking, bool acknowledgeLaunchMayStillOccur, CancellationToken token)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var tracking = _tracking;
            if (tracking == null) throw new ArgumentException("There is no Ctrl+F5 request to reconcile.");
            tracking.RequireId(requestId);
            if (abandonTracking && !acknowledgeLaunchMayStillOccur)
                throw new ArgumentException("Explicitly acknowledgeLaunchMayStillOccur before abandoning tracking. This does not cancel VS's queued command.");
            if (tracking.IsPending) await RefreshAsync(tracking, _status, _expected, _requested, token);
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            // A newer request may have started while the process query was in flight.
            if (!ReferenceEquals(_tracking, tracking)) throw new ArgumentException("Request is no longer current; read get_debug_status.");
            if (abandonTracking && tracking.IsPending)
            {
                if (_vs.TestsRunning() || BuildRunner.IsRunning || BuildRunner.IsBuildManagerBusy() || _vs.Dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
                    throw new McpToolException(ToolErrorCode.ExecutionError, "Wait for the active build, tests or debugger before abandoning launch tracking.");
                tracking.Abandon(acknowledgeLaunchMayStillOccur);
                _status["observation_active"] = false;
                _status["message"] = "Tracking abandoned explicitly. The VS command was NOT cancelled and may still launch a program. No process was terminated. Inspect VS before another launch.";
            }
            return Status;
        }

        private sealed class Observation
        {
            public JArray Processes;
            public string Error;
        }

        private static Observation ReadProcesses(HashSet<string> expected, DateTime requested)
        {
            try
            {
                using (var search = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, ExecutablePath, CreationDate FROM Win32_Process"))
                using (var rows = search.Get())
                {
                    var snapshot = new List<LaunchProcess>();
                    foreach (ManagementObject row in rows)
                    using (row)
                    {
                        var created = row["CreationDate"] as string;
                        snapshot.Add(new LaunchProcess {
                            Pid = Convert.ToInt32(row["ProcessId"]), ParentPid = Convert.ToInt32(row["ParentProcessId"]),
                            Path = row["ExecutablePath"] as string,
                            Created = created == null ? default : ManagementDateTimeConverter.ToDateTime(created).ToUniversalTime(),
                        });
                    }
                    return new Observation { Processes = new JArray(LaunchObservation.Match(snapshot, System.Diagnostics.Process.GetCurrentProcess().Id, requested, expected)
                        .Select(p => new JObject { ["pid"] = p.Pid, ["path"] = p.Path })) };
                }
            }
            catch (Exception ex) { return new Observation { Error = ex.Message }; }
        }

        private async Task RefreshAsync(LaunchTracking tracking, JObject status, HashSet<string> expected, DateTime requested, CancellationToken token)
        {
            await TaskScheduler.Default;
            token.ThrowIfCancellationRequested();
            var observation = ReadProcesses(expected, requested);
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (!ReferenceEquals(_tracking, tracking) || !tracking.IsPending) return;
            status["last_checked_at"] = DateTime.UtcNow.ToString("O");
            status["observation_error"] = observation.Error;
            if (observation.Processes == null) return;
            status["processes"] = observation.Processes;
            if (expected.All(path => observation.Processes.OfType<JObject>().Any(p => string.Equals((string)p["path"], path, StringComparison.OrdinalIgnoreCase))))
            {
                tracking.Confirm();
                status["observation_active"] = false;
                status["message"] = "Observed new VS descendant processes matching every expected executable. This confirms launch, not application readiness.";
            }
        }

        private async Task ObserveAsync(LaunchTracking tracking, JObject status, HashSet<string> expected, DateTime requested)
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    await RefreshAsync(tracking, status, expected, requested, CancellationToken.None);
                    await _vs.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!ReferenceEquals(_tracking, tracking) || !tracking.IsPending) return;
                    await Task.Delay(500);
                }
                await _vs.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ReferenceEquals(_tracking, tracking) && tracking.IsPending)
                    status["message"] = "Launch unconfirmed after 120 seconds; further launches remain blocked. Use without_debugging_reconcile to recheck, or explicitly abandon tracking after inspecting VS. Short-lived processes or detached launchers may be missed.";
            }
            catch (Exception ex)
            {
                await _vs.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ReferenceEquals(_tracking, tracking) && tracking.IsPending) status["observation_error"] = ex.Message;
            }
            finally
            {
                await _vs.JoinableTaskFactory.SwitchToMainThreadAsync();
                status["observation_active"] = false;
            }
        }
    }
}
