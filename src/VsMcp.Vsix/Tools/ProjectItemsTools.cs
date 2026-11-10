using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using VsMcp.Contracts;
using VsMcp.Mcp;
using VsMcp.Services.VisualStudio;

namespace VsMcp.Tools
{
    /// <summary>Read-only bounded views of a project's IDE item hierarchy.</summary>
    internal sealed class ProjectItemsTools
    {
        private const int MaxItems = 2000;
        private const int MaxDepth = 64;
        private readonly VsContext _vs;
        public ProjectItemsTools(VsContext vs) => _vs = vs;

        [McpServerTool(Name = "project_items", Title = "Get project items", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Enumerate one project's Visual Studio item hierarchy, including folders and physical file paths when available. Requires an exact project name or unique name; ambiguous and missing projects fail. Results are bounded to 2000 items and report truncation. Read-only.")]
        public async Task<GetProjectItemsOutput> ProjectItemsTool([Description("Exact project name or unique name (relative project path).") ] string project, CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var instance = _vs.Instance();
            if (!_vs.Dte.Solution.IsOpen)
                throw new McpToolException(ToolErrorCode.NoSolution, "No solution is open in this Visual Studio instance.", new JObject { ["instance"] = instance });
            var projects = _vs.EnumerateProjects();
            var uniqueName = VsContext.ResolveProject(projects, project, instance);
            if (uniqueName == null)
                throw new McpToolException(ToolErrorCode.ProjectNotFound, "A project name or unique name is required.", new JObject { ["instance"] = instance });
            var dteProject = FindProject(_vs.Dte.Solution.Projects, uniqueName);
            if (dteProject == null)
                throw new McpToolException(ToolErrorCode.ProjectNotFound, $"Project '{uniqueName}' is not available in this Visual Studio instance.", new JObject { ["instance"] = instance });

            var items = new List<ProjectItemInfo>();
            var truncated = false;
            Walk(dteProject.ProjectItems, null, 0, items, ref truncated);
            return new GetProjectItemsOutput { Instance = _vs.ReadInstance(), Project = uniqueName, Items = items, Truncated = truncated };
        }

        private static Project FindProject(Projects projects, string uniqueName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (Project project in projects)
            {
                if (project == null) continue;
                if (VsContext.Same(project.UniqueName, uniqueName)) return project;
                if (string.Equals(project.Kind, "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}", StringComparison.OrdinalIgnoreCase))
                {
                    var nested = FindProjectItems(project.ProjectItems, uniqueName);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        private static Project FindProjectItems(ProjectItems items, string uniqueName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (ProjectItem item in items)
            {
                if (item.SubProject == null) continue;
                if (VsContext.Same(item.SubProject.UniqueName, uniqueName)) return item.SubProject;
                var nested = FindProjectItems(item.SubProject.ProjectItems, uniqueName);
                if (nested != null) return nested;
            }
            return null;
        }

        private static void Walk(ProjectItems children, string parent, int depth, List<ProjectItemInfo> result, ref bool truncated)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (ProjectItem item in children)
            {
                if (result.Count == MaxItems) { truncated = true; return; }
                var path = string.IsNullOrEmpty(parent) ? item.Name : parent + "/" + item.Name;
                var nested = item.ProjectItems;
                var hasChildren = nested != null && nested.Count > 0;
                var kind = item.Kind;
                var isPhysicalFile = string.Equals(kind, VSConstants.GUID_ItemType_PhysicalFile.ToString("B"), StringComparison.OrdinalIgnoreCase);
                var isFolder = item.SubProject != null || string.Equals(kind, VSConstants.GUID_ItemType_PhysicalFolder.ToString("B"), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kind, VSConstants.GUID_ItemType_VirtualFolder.ToString("B"), StringComparison.OrdinalIgnoreCase)
                    || (!isPhysicalFile && hasChildren);
                string filePath = null;
                try { if (item.FileCount > 0) filePath = item.FileNames[1]; } catch (Exception) { }
                result.Add(new ProjectItemInfo { Name = item.Name, Path = path, Kind = isFolder ? "folder" : "file", FilePath = filePath });
                if (hasChildren)
                {
                    if (depth + 1 >= MaxDepth) truncated = true;
                    else Walk(nested, path, depth + 1, result, ref truncated);
                }
                if (truncated) return;
            }
        }
    }
}
