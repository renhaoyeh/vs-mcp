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
        [McpServerTool(Name = "startup_project_get", Title = "startup_project_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Read startup project unique names for the current solution.")]
        public async Task<CallToolResult> StartupAsync(CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(new { projects = vs.Dte.Solution.SolutionBuild.StartupProjects });
        }
        [McpServerTool(Name = "startup_project_set", Title = "startup_project_set"), Description("Explicitly set a single startup project by exact name or unique name. Requires idle IDE; ambiguous names fail.")]
        public async Task<CallToolResult> SetStartupAsync(string project, CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); Idle();
            if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Project is required.");
            var unique = VsContext.ResolveProject(vs.EnumerateProjects(), project, vs.Instance());
            vs.Dte.Solution.SolutionBuild.StartupProjects = new[] { unique }; return Result(new { projects = vs.Dte.Solution.SolutionBuild.StartupProjects });
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
