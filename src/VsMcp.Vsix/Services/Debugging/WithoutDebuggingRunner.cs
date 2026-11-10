using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public bool IsPending => _status != null && (string)_status["status"] == "unconfirmed";

        public WithoutDebuggingRunner(VsContext vs) { _vs = vs; }

        public JObject Status => _status == null ? null : (JObject)_status.DeepClone();

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
                    var outputs = project.ConfigurationManager.ActiveConfiguration.OutputGroups.Item("Built").FileURLs as Array;
                    if (outputs != null) foreach (var output in outputs)
                    {
                        var value = Convert.ToString(output);
                        var path = Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : Path.GetFullPath(value);
                        if (string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase)) path = Path.ChangeExtension(path, ".exe");
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
            _status = new JObject { ["instance"] = _vs.Instance(), ["status"] = "unconfirmed", ["startup_projects"] = new JArray(startup),
                ["solution_configuration"] = active.Name, ["platform"] = active.PlatformName, ["expected_paths"] = new JArray(_expected),
                ["processes"] = new JArray(), ["requested_at"] = _requested.ToString("O"),
                ["message"] = "Command acceptance is not success. Do not retry an unconfirmed request; inspect VS and get_debug_status." };
            try
            {
                var group = VSConstants.GUID_VSStandardCommandSet97;
                object input = null;
                ErrorHandler.ThrowOnFailure(((IVsUIShell)Package.GetGlobalService(typeof(SVsUIShell))).PostExecCommand(ref group, (uint)VSConstants.VSStd97CmdID.StartNoDebug, 0, ref input));
            }
            catch (Exception ex)
            {
                _status["status"] = "failed";
                _status["message"] = ex.Message;
                throw new McpToolException(ToolErrorCode.DebugStartFailed, ex.Message, Status);
            }
            // Observation runs independently of the caller's lifetime, and never accesses DTE off-thread.
            var host = System.Diagnostics.Process.GetCurrentProcess().Id;
            _observation = ObserveAsync(host);
            await Task.WhenAny(_observation, Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Status;
        }

        private async Task ObserveAsync(int host)
        {
            await TaskScheduler.Default;
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < deadline)
            {
                JArray found = null;
                string error = null;
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
                                Pid = Convert.ToInt32(row["ProcessId"]),
                                ParentPid = Convert.ToInt32(row["ParentProcessId"]),
                                Path = row["ExecutablePath"] as string,
                                Created = created == null ? default : ManagementDateTimeConverter.ToDateTime(created).ToUniversalTime(),
                            });
                        }
                        found = new JArray(LaunchObservation.Match(snapshot, host, _requested, _expected)
                            .Select(p => new JObject { ["pid"] = p.Pid, ["path"] = p.Path }));
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                await _vs.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (found != null) _status["processes"] = found;
                if (error != null) _status["observation_error"] = error;
                if (found != null && _expected.All(path => found.OfType<JObject>().Any(p => string.Equals((string)p["path"], path, StringComparison.OrdinalIgnoreCase))))
                {
                    _status["status"] = "started";
                    _status["message"] = "Observed new VS descendant processes matching every expected executable. This is launch confirmation, not application readiness.";
                    return;
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync();
            _status["message"] = "Launch could not be confirmed within 120 seconds; request remains unresolved and blocks further launches. Inspect VS; restart the extension after resolving it. Short-lived processes or detached launchers may not be observable.";
        }
    }
}
