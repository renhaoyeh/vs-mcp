using VsMcp.Services.VisualStudio;
using VsMcp.Services.Debugging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using VsMcp.Contracts;
using VsMcp.Mcp;

namespace VsMcp.Services.VisualStudio
{
    /// <summary>The Visual Studio instance the tools work on, and the solution queries they share. DTE access needs the UI thread.</summary>
    internal sealed class VsContext
    {
        private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
        private const string MiscFilesKind = "{66A2671D-8FB5-11D2-AA7E-00C04F688DDE}";

        private readonly DTE2 _dte;
        private readonly JoinableTaskFactory _joinableTaskFactory;
        private DebugRunner _debugRunner;

        public VsContext(DTE2 dte, JoinableTaskFactory joinableTaskFactory)
        {
            _dte = dte;
            _joinableTaskFactory = joinableTaskFactory;
        }

        public DTE2 Dte => _dte;

        public JoinableTaskFactory JoinableTaskFactory => _joinableTaskFactory;
        public Func<bool> TestsRunning { get; set; } = () => false;

        /// <summary>True while a debug launch has been posted and not yet resolved.</summary>
        public bool DebugLaunchPending => _debugRunner != null && _debugRunner.IsPending;

        public DebugRunner Debug() => _debugRunner ?? (_debugRunner = new DebugRunner(_dte, _joinableTaskFactory));

        internal JObject Instance()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ToolJson.ToJObject(ReadInstance());
        }

        internal InstanceInfo ReadInstance()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var process = System.Diagnostics.Process.GetCurrentProcess();
            string executable = null;
            string fileVersion = null;
            try
            {
                executable = process.MainModule.FileName;
                fileVersion = FileVersionInfo.GetVersionInfo(executable).FileVersion;
            }
            catch (Exception)
            {
                // Module information can be unavailable; the PID and DTE version remain.
            }

            var solutionOpen = _dte.Solution.IsOpen;
            var solutionPath = solutionOpen ? _dte.Solution.FullName : null;
            return new InstanceInfo
            {
                Pid = process.Id,
                Product = _dte.Name,
                DteVersion = _dte.Version,
                Version = fileVersion,
                ExecutablePath = executable,
                SolutionOpen = solutionOpen,

                // An unsaved solution is open but has no file path yet.
                SolutionPath = string.IsNullOrEmpty(solutionPath) ? null : solutionPath,
            };
        }

        internal static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Resolves an optional project filter (name or unique name) to exactly one project's unique name,
        /// or null when no filter was given. A miss or an ambiguous match fails instead of guessing.
        /// </summary>
        internal static string ResolveProject(List<ProjectEntry> projects, string wantedProject, JObject instance)
        {
            if (wantedProject == null) return null;

            var matches = projects
                .Where(p => Same(p.Name, wantedProject) || Same(p.UniqueName, wantedProject))
                .Select(p => p.UniqueName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matches.Count == 0)
            {
                throw new McpToolException(ToolErrorCode.ProjectNotFound, $"No project named '{wantedProject}' in the solution.",
                    new JObject { ["instance"] = instance, ["available_projects"] = new JArray(projects.Select(p => p.UniqueName)) });
            }

            if (matches.Count > 1)
            {
                throw new McpToolException(ToolErrorCode.AmbiguousProject, $"'{wantedProject}' matches more than one project; use a unique name.",
                    new JObject { ["instance"] = instance, ["matches"] = new JArray(matches) });
            }

            return matches[0];
        }

        /// <summary>Every project in the solution, with solution folders flattened away.</summary>
        internal List<ProjectEntry> EnumerateProjects()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<ProjectEntry>();
            Flatten(BuildTree(), result);
            return result;
        }

        internal static void Flatten(IEnumerable<TreeNode> nodes, List<ProjectEntry> result)
        {
            foreach (var node in nodes)
            {
                if (node.IsFolder) Flatten(node.Children, result);
                else result.Add(node.Project);
            }
        }

        internal List<TreeNode> BuildTree()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var roots = new List<TreeNode>();
            foreach (Project project in _dte.Solution.Projects)
            {
                AddNode(project, roots);
            }

            return roots;
        }

        internal static void AddNode(Project project, List<TreeNode> siblings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project == null) return;

            string kind;
            try { kind = project.Kind; }
            catch (Exception) { return; }

            // The "Miscellaneous Files" pseudo-project is Visual Studio's bucket for loose files, not a project.
            if (string.Equals(kind, MiscFilesKind, StringComparison.OrdinalIgnoreCase)) return;

            if (string.Equals(kind, SolutionFolderKind, StringComparison.OrdinalIgnoreCase))
            {
                var folder = new TreeNode { Name = project.Name };
                foreach (ProjectItem item in project.ProjectItems)
                {
                    AddNode(item.SubProject, folder.Children);
                }

                siblings.Add(folder);
                return;
            }

            string fullName = null;
            try { fullName = project.FullName; }
            catch (Exception) { /* unloaded projects have no file name */ }

            siblings.Add(new TreeNode
            {
                Name = project.Name,
                Project = new ProjectEntry { Name = project.Name, UniqueName = project.UniqueName, FullName = fullName, Kind = kind },
            });
        }

        /// <summary>Walks down a '/'-separated solution folder path one name at a time; a miss fails instead of guessing.</summary>
        internal TreeNode ResolveFolder(string folderPath, JObject instance, out List<string> walked)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var level = BuildTree();
            walked = new List<string>();
            TreeNode node = null;
            foreach (var part in folderPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                node = level.FirstOrDefault(n => n.IsFolder && Same(n.Name, part));
                if (node == null)
                {
                    throw new McpToolException(ToolErrorCode.FolderNotFound,
                        $"No solution folder '{part}' under '{(walked.Count == 0 ? "(solution root)" : string.Join("/", walked))}'.",
                        new JObject { ["instance"] = instance, ["available_folders"] = new JArray(level.Where(n => n.IsFolder).Select(n => n.Name)) });
                }

                walked.Add(node.Name);
                level = node.Children;
            }

            if (node == null)
            {
                throw new McpToolException(ToolErrorCode.FolderNotFound, "A solution folder path is required.", new JObject { ["instance"] = instance });
            }

            return node;
        }

        /// <summary>
        /// The solution's direct build dependencies as Visual Studio records them: project unique name to the unique
        /// names of the projects it requires.
        /// </summary>
        internal Dictionary<string, List<string>> ReadDirectDependencies()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var direct = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (BuildDependency dependency in _dte.Solution.SolutionBuild.BuildDependencies)
            {
                var required = new List<string>();
                if (dependency.RequiredProjects is Array array)
                {
                    foreach (var item in array)
                    {
                        if (item is Project requiredProject) required.Add(requiredProject.UniqueName);
                    }
                }

                direct[dependency.Project.UniqueName] = required;
            }

            return direct;
        }

        /// <summary>
        /// Orders the given projects so each follows the projects it requires that are also in the set; the tree order is
        /// kept otherwise. Projects outside the set are not added.
        /// </summary>
        /// <summary>
        /// The requested projects plus every project they require, directly or indirectly, ordered so a project follows
        /// the projects it requires. This is what Visual Studio builds when you build a project or a solution folder.
        /// </summary>
        internal List<string> OrderWithDependencies(List<string> requested)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var direct = ReadDirectDependencies();
            var ordered = new List<string>();
            var state = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Visit(string name)
            {
                if (!state.Add(name)) return; // done, or a cycle that Visual Studio does not allow anyway
                if (direct.TryGetValue(name, out var required))
                {
                    foreach (var dependency in required) Visit(dependency);
                }

                ordered.Add(name);
            }

            foreach (var name in requested) Visit(name);
            return ordered;
        }

        internal List<string> OrderByDependencies(List<ProjectEntry> entries)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var direct = ReadDirectDependencies();
            var members = new HashSet<string>(entries.Select(e => e.UniqueName), StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Visit(string name)
            {
                if (!visited.Add(name)) return; // already visited, or a cycle that Visual Studio does not allow anyway
                if (direct.TryGetValue(name, out var required))
                {
                    foreach (var dependency in required)
                    {
                        if (members.Contains(dependency)) Visit(dependency);
                    }
                }

                ordered.Add(name);
            }

            foreach (var entry in entries) Visit(entry.UniqueName);
            return ordered;
        }
    }

    internal sealed class ProjectEntry
    {
        public string Name;
        public string UniqueName;
        public string FullName;
        public string Kind;
    }

    /// <summary>A solution folder or a project in the solution's tree.</summary>
    internal sealed class TreeNode
    {
        public string Name;
        public ProjectEntry Project;
        public readonly List<TreeNode> Children = new List<TreeNode>();

        public bool IsFolder => Project == null;
    }
}
