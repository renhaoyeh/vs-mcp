using VsMcp.Services.VisualStudio;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using VsMcp.Mcp;

namespace VsMcp.Tools
{
    internal sealed class SolutionManagementTools
    {
        private readonly VsContext vs;
        public SolutionManagementTools(VsContext context) { vs = context; }
        private CallToolResult Result(object data) { ThreadHelper.ThrowIfNotOnUIThread(); return ToolJson.Result(new JObject { ["instance"] = vs.Instance(), ["data"] = JToken.Parse(System.Text.Json.JsonSerializer.Serialize(data, ToolJson.Options)) }); }
        private void Idle()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (vs.TestsRunning() || vs.Dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode || vs.DebugLaunchPending || vs.Dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
                throw new InvalidOperationException("Operation requires an idle IDE with no pending launch, build or debug session.");
        }
        private void Saved()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (vs.Dte.Documents.Cast<Document>().Any(d => { ThreadHelper.ThrowIfNotOnUIThread(); return !d.Saved; }) || (vs.Dte.Solution.IsOpen && !vs.Dte.Solution.Saved))
                throw new InvalidOperationException("Save all documents and the solution before switching solutions.");
        }
        [McpServerTool(Name = "solution_open", Title = "solution_open", Destructive = true), Description("Open an existing absolute solution path. Rejects builds, debugging and unsaved documents or solutions; never discards changes.")]
        public async Task<CallToolResult> OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!Path.IsPathRooted(path) || !File.Exists(path) || !(path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("An existing absolute .sln or .slnx path is required.");
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); Idle(); Saved();
            vs.Dte.Solution.Open(path); return Result(new { opened = vs.Dte.Solution.IsOpen });
        }
        [McpServerTool(Name = "solution_close", Title = "solution_close", Destructive = true), Description("Close the current solution only when idle and all documents and the solution are saved.")]
        public async Task<CallToolResult> CloseAsync(CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); Idle(); Saved();
            vs.Dte.Solution.Close(false); return Result(new { closed = !vs.Dte.Solution.IsOpen });
        }
        [McpServerTool(Name = "project_unload", Title = "Unload project", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Unload one project by exact name or unique name while keeping it in the solution. Requires an idle IDE and saved documents, solution and target project. Missing or ambiguous names fail. Never removes project files. A cancelled or interrupted call must be inspected before retrying.")]
        public async Task<CallToolResult> UnloadProjectAsync([Description("Exact project name or unique name (relative project path), as returned by get_projects.")] string project, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Project is required.");
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Idle();
            if (!vs.Dte.Solution.IsOpen) throw new InvalidOperationException("No solution is open in this Visual Studio instance.");
            if (vs.Dte.Documents.Cast<Document>().Any(d => { ThreadHelper.ThrowIfNotOnUIThread(); return !d.Saved; }) || !vs.Dte.Solution.Saved)
                throw new InvalidOperationException("Save all documents and the solution before unloading a project.");
            var unique = VsContext.ResolveProject(vs.EnumerateProjects(), project, vs.Instance());
            var solution = ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution)) as IVsSolution;
            if (solution == null) throw new NotSupportedException("The Visual Studio solution service is unavailable.");
            if (!(solution is IVsSolution4 solution4)) throw new NotSupportedException("This Visual Studio instance does not support unloading projects.");
            ErrorHandler.ThrowOnFailure(solution.GetProjectOfUniqueName(unique, out var hierarchy));
            ErrorHandler.ThrowOnFailure(solution.GetGuidOfProject(hierarchy, out var guid));
            if (IsUnloaded(solution, guid)) throw new InvalidOperationException("The project is already unloaded. Use project_reload to load it.");
            ErrorHandler.ThrowOnFailure(hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ExtObject, out var extObject));
            if (!(extObject is Project target) || !target.Saved)
                throw new InvalidOperationException("The target project must be loaded and saved before unloading.");
            cancellationToken.ThrowIfCancellationRequested();
            var hr = solution4.UnloadProject(ref guid, (uint)_VSProjectUnloadStatus.UNLOADSTATUS_UnloadedByUser);
            ErrorHandler.ThrowOnFailure(hr);
            if (hr != VSConstants.S_OK || !IsUnloaded(solution, guid)) throw new InvalidOperationException("Visual Studio did not complete the project unload. Inspect its state before retrying.");
            return Result(new { project = unique, projectGuid = guid, unloaded = true });
        }
        [McpServerTool(Name = "project_reload", Title = "Reload unloaded project", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Reload one unloaded project by exact name or unique name, like Reload Project in Solution Explorer. Requires an idle IDE and saved documents and solution. Missing, ambiguous or already loaded projects fail. Does not reload a loaded project or change configuration. Inspect state before retrying an interrupted call.")]
        public async Task<CallToolResult> ReloadProjectAsync([Description("Exact project name or unique name (relative project path), as returned by get_projects or project_unload.")] string project, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Project is required.");
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Idle();
            if (!vs.Dte.Solution.IsOpen) throw new InvalidOperationException("No solution is open in this Visual Studio instance.");
            if (vs.Dte.Documents.Cast<Document>().Any(d => { ThreadHelper.ThrowIfNotOnUIThread(); return !d.Saved; }) || !vs.Dte.Solution.Saved)
                throw new InvalidOperationException("Save all documents and the solution before reloading a project.");
            var unique = VsContext.ResolveProject(vs.EnumerateProjects(), project, vs.Instance());
            var solution = ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution)) as IVsSolution;
            if (solution == null || !(solution is IVsSolution4 solution4)) throw new NotSupportedException("This Visual Studio instance does not support reloading projects.");
            ErrorHandler.ThrowOnFailure(solution.GetProjectOfUniqueName(unique, out var hierarchy));
            ErrorHandler.ThrowOnFailure(solution.GetGuidOfProject(hierarchy, out var guid));
            if (!IsUnloaded(solution, guid)) throw new InvalidOperationException("The project is already loaded. Only unloaded projects can be reloaded with project_reload.");
            cancellationToken.ThrowIfCancellationRequested();
            var hr = solution4.ReloadProject(ref guid);
            ErrorHandler.ThrowOnFailure(hr);
            if (hr != VSConstants.S_OK || IsUnloaded(solution, guid)) throw new InvalidOperationException("Visual Studio did not complete the project reload. Inspect its state before retrying.");
            return Result(new { project = unique, projectGuid = guid, loaded = true });
        }
        private static bool IsUnloaded(IVsSolution solution, Guid guid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var type = Guid.Empty;
            ErrorHandler.ThrowOnFailure(solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_UNLOADEDINSOLUTION, ref type, out var projects));
            var items = new IVsHierarchy[1];
            while (true)
            {
                var hr = projects.Next(1, items, out var fetched);
                ErrorHandler.ThrowOnFailure(hr);
                if (fetched == 0) return false;
                ErrorHandler.ThrowOnFailure(solution.GetGuidOfProject(items[0], out var current));
                if (current == guid) return true;
            }
        }
        [McpServerTool(Name = "startup_project_get", Title = "startup_project_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Read startup project unique names for the current solution.")]
        public async Task<CallToolResult> StartupAsync(CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(new { projects = vs.Dte.Solution.SolutionBuild.StartupProjects });
        }
        [McpServerTool(Name = "startup_project_set", Title = "startup_project_set"), Description("Explicitly set startup projects in the connected idle IDE. Supply project for one project, or projects for an ordered list (an explicit empty list clears startup projects), never both. Every exact name/unique name must resolve uniquely before any write; duplicates are rejected. Reads back the complete startup list and returns previous values. Does not start debugging, build or switch configuration. After interruption inspect startup_project_get before retrying.")]
        public async Task<CallToolResult> SetStartupAsync(string project = null, string[] projects = null, CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); Idle();
            if ((project == null) == (projects == null))
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Supply either project or projects, not both.", new JObject { ["instance"] = vs.Instance() });
            var requested = projects ?? new[] { project };
            if (!vs.Dte.Solution.IsOpen)
                throw new McpToolException(ToolErrorCode.NoSolution, "Open a solution before setting startup projects.", new JObject { ["instance"] = vs.Instance() });
            if (requested.Length > 100 || requested.Any(string.IsNullOrWhiteSpace))
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Supply at most 100 nonempty project names.", new JObject { ["instance"] = vs.Instance() });
            var available = vs.EnumerateProjects().ToList();
            var resolved = requested.Select(name => { ThreadHelper.ThrowIfNotOnUIThread(); return VsContext.ResolveProject(available, name, vs.Instance()); }).ToArray();
            if (resolved.Distinct(StringComparer.OrdinalIgnoreCase).Count() != resolved.Length)
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Duplicate startup projects are not allowed.", new JObject { ["instance"] = vs.Instance() });
            var previous = (vs.Dte.Solution.SolutionBuild.StartupProjects as Array)?.Cast<object>().Select(Convert.ToString).ToArray() ?? Array.Empty<string>();
            cancellationToken.ThrowIfCancellationRequested();
            vs.Dte.Solution.SolutionBuild.StartupProjects = resolved;
            var actual = (vs.Dte.Solution.SolutionBuild.StartupProjects as Array)?.Cast<object>().Select(Convert.ToString).ToArray() ?? Array.Empty<string>();
            if (!actual.SequenceEqual(resolved, StringComparer.OrdinalIgnoreCase))
                throw new McpToolException(ToolErrorCode.ExecutionError, "Startup project update was not confirmed. Read startup_project_get before retrying.",
                    new JObject { ["instance"] = vs.Instance(), ["previous"] = new JArray(previous), ["projects"] = new JArray(actual) });
            return Result(new { projects = actual, previous });
        }
        [McpServerTool(Name = "build_configuration_set", Title = "build_configuration_set"), Description("Explicitly activate an exact solution configuration and platform pair while idle. No fallback; builds themselves never switch configuration.")]
        public async Task<CallToolResult> ConfigurationAsync(string configuration, string platform, CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); Idle();
            var pair = vs.Dte.Solution.SolutionBuild.SolutionConfigurations.Cast<SolutionConfiguration2>().SingleOrDefault(c => { ThreadHelper.ThrowIfNotOnUIThread(); return VsContext.Same(c.Name, configuration) && VsContext.Same(c.PlatformName, platform); });
            if (pair == null) throw new ArgumentException("Configuration/platform pair does not exist. Read get_solution_configurations.");
            pair.Activate(); return Result(new { configuration = pair.Name, platform = pair.PlatformName });
        }
    }
}
