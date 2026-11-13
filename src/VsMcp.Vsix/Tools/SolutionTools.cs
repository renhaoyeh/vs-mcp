using VsMcp.Services.VisualStudio;
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

namespace VsMcp.Tools
{
    /// <summary>Read-only queries about the open solution: the instance, projects and folders, configurations and dependencies.</summary>
    internal sealed class SolutionTools
    {
        private readonly VsContext _vs;
        private readonly DTE2 _dte;
        private readonly JoinableTaskFactory _joinableTaskFactory;

        public SolutionTools(VsContext vs)
        {
            _vs = vs;
            _dte = vs.Dte;
            _joinableTaskFactory = vs.JoinableTaskFactory;
        }

        [McpServerTool(Name = "get_vs_info", Title = "Get Visual Studio instance info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Identify the Visual Studio instance serving this connection: process ID, product version and the open solution path. Call this first and confirm it is the intended instance before relying on any other result.")]
        public async Task<InstanceInfo> GetVsInfoTool(CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return _vs.ReadInstance();
        }

        [McpServerTool(Name = "get_projects", Title = "Get projects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("List the solution's projects together with its solution-folder hierarchy, one level at a time. Without 'folder' it returns the top level of the solution; with 'folder' (a path of solution folder names separated by '/') it returns what is directly inside that folder. Each folder reports its direct folder and project counts and the total number of projects below it, so you can decide where to go deeper. Set 'recursive' to expand the whole subtree at once (large solutions can be big). Each project reports isLoaded (false means unloaded; call project_reload, verify loaded, then build with VS MCP). Read-only. A folder path that does not exist fails with the available folders instead of falling back.")]
        public async Task<GetProjectsOutput> GetProjectsTool([Description("Solution folder path to look inside, for example 'FolderA/SubFolder'. Omit for the top level.")] string folder = null, [Description("Expand every folder below the requested level.")] bool recursive = false, CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return GetProjects(folder, recursive);
        }

        [McpServerTool(Name = "get_solution_configurations", Title = "Get solution configurations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("List the open solution's solution configuration and platform pairs, and for each pair the project configuration and platform every project actually maps to. Use get_projects for the project list and folder hierarchy. Read-only: never changes the active configuration. Optional filters match exactly (case-insensitive) and fail instead of falling back to another value.")]
        public async Task<GetSolutionConfigurationsOutput> GetSolutionConfigurationsTool([Description("Only this solution configuration name.")] string solutionConfiguration = null, [Description("Only this solution platform, for example 'x64'.")] string platform = null, [Description("Only this project, by name or unique name (relative project path).")] string project = null, CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return GetSolutionConfigurations(solutionConfiguration, platform, project);
        }

        [McpServerTool(Name = "get_project_dependencies", Title = "Get project dependencies", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("List, for each project (or one project), the projects it directly requires and every project that must be built before it (direct and indirect). Building a project builds these too. Dependencies come from the solution's build dependencies and do not depend on the configuration; whether a dependency is built in a given configuration is reported by get_solutionConfigurations (shouldBuild). Read-only. The optional project filter matches exactly (case-insensitive) and fails instead of falling back.")]
        public async Task<GetProjectDependenciesOutput> GetProjectDependenciesTool([Description("Only this project, by name or unique name (relative project path).")] string project = null, CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return GetProjectDependencies(project);
        }

        private GetSolutionConfigurationsOutput GetSolutionConfigurations(string wantedConfiguration, string wantedPlatform, string wantedProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var instance = _vs.Instance();
            if (!_dte.Solution.IsOpen)
            {
                throw new McpToolException(ToolErrorCode.NoSolution, "No solution is open in this Visual Studio instance.", new JObject { ["instance"] = instance });
            }

            var projects = _vs.EnumerateProjects();
            var byUniqueName = projects.GroupBy(p => p.UniqueName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var selectedUniqueName = VsContext.ResolveProject(projects, wantedProject, instance);

            var build = _dte.Solution.SolutionBuild;
            var active = build.ActiveConfiguration as SolutionConfiguration2;
            var configurations = new List<SolutionConfigurationInfo>();
            var available = new List<string>();

            foreach (SolutionConfiguration2 configuration in build.SolutionConfigurations)
            {
                var configurationName = configuration.Name;
                var platformName = configuration.PlatformName;
                available.Add(configurationName + " | " + platformName);

                if (wantedConfiguration != null && !VsContext.Same(configurationName, wantedConfiguration)) continue;
                if (wantedPlatform != null && !VsContext.Same(platformName, wantedPlatform)) continue;

                var contexts = new List<ProjectMapping>();
                foreach (SolutionContext context in configuration.SolutionContexts)
                {
                    var uniqueName = context.ProjectName;
                    if (selectedUniqueName != null && !VsContext.Same(uniqueName, selectedUniqueName)) continue;

                    byUniqueName.TryGetValue(uniqueName, out var project);
                    contexts.Add(new ProjectMapping
                    {
                        Project = project?.Name ?? uniqueName,
                        UniqueName = uniqueName,
                        ProjectConfiguration = context.ConfigurationName,
                        ProjectPlatform = context.PlatformName,
                        ShouldBuild = context.ShouldBuild,
                        ShouldDeploy = context.ShouldDeploy,
                    });
                }

                configurations.Add(new SolutionConfigurationInfo
                {
                    SolutionConfiguration = configurationName,
                    SolutionPlatform = platformName,
                    IsActive = active != null && VsContext.Same(active.Name, configurationName) && VsContext.Same(active.PlatformName, platformName),
                    Projects = contexts,
                });
            }

            if (configurations.Count == 0)
            {
                throw new McpToolException(ToolErrorCode.ConfigurationNotFound, "No solution configuration matches the requested filter.",
                    new JObject { ["instance"] = instance, ["available_configurations"] = new JArray(available) });
            }

            return new GetSolutionConfigurationsOutput
            {
                Instance = _vs.ReadInstance(),
                ActiveConfiguration = active == null ? null : new ActiveConfiguration { SolutionConfiguration = active.Name, SolutionPlatform = active.PlatformName },
                SolutionConfigurations = configurations,
            };
        }

        private GetProjectDependenciesOutput GetProjectDependencies(string wantedProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var instance = _vs.Instance();
            if (!_dte.Solution.IsOpen)
            {
                throw new McpToolException(ToolErrorCode.NoSolution, "No solution is open in this Visual Studio instance.", new JObject { ["instance"] = instance });
            }

            var projects = _vs.EnumerateProjects();
            var byUniqueName = projects.GroupBy(p => p.UniqueName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var selectedUniqueName = VsContext.ResolveProject(projects, wantedProject, instance);

            // Direct requirements as Visual Studio records them in the solution's build dependencies.
            var direct = _vs.ReadDirectDependencies();

            ProjectName Describe(string uniqueName)
            {
                var found = byUniqueName.TryGetValue(uniqueName, out var entry);
                return new ProjectName { Name = found ? entry.Name : uniqueName, UniqueName = uniqueName };
            }

            var result = new List<ProjectDependencies>();
            foreach (var project in projects)
            {
                if (selectedUniqueName != null && !VsContext.Same(project.UniqueName, selectedUniqueName)) continue;

                direct.TryGetValue(project.UniqueName, out var directNames);
                directNames = directNames ?? new List<string>();

                // Everything that must be built before this project: direct requirements plus theirs, without repeats.
                var all = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.UniqueName };
                var pending = new Stack<string>(Enumerable.Reverse(directNames));
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    if (!seen.Add(current)) continue;
                    all.Add(current);
                    if (direct.TryGetValue(current, out var next))
                    {
                        foreach (var name in Enumerable.Reverse(next)) pending.Push(name);
                    }
                }

                result.Add(new ProjectDependencies
                {
                    Name = project.Name,
                    UniqueName = project.UniqueName,
                    DirectDependencies = directNames.Select(Describe).ToList(),
                    AllDependencies = all.Select(Describe).ToList(),
                });
            }

            return new GetProjectDependenciesOutput { Instance = _vs.ReadInstance(), Projects = result };
        }

        private GetProjectsOutput GetProjects(string folder, bool recursive)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var instance = _vs.Instance();
            if (!_dte.Solution.IsOpen)
            {
                throw new McpToolException(ToolErrorCode.NoSolution, "No solution is open in this Visual Studio instance.", new JObject { ["instance"] = instance });
            }

            var folderPath = (folder ?? "").Trim().Trim('/');

            var level = _vs.BuildTree();
            var walked = new List<string>();
            if (folderPath.Length > 0)
            {
                level = _vs.ResolveFolder(folderPath, instance, out walked).Children;
            }

            var result = Level(level, walked, recursive);
            return new GetProjectsOutput
            {
                Instance = _vs.ReadInstance(),
                Folder = walked.Count == 0 ? null : string.Join("/", walked),
                Folders = result.Folders,
                Projects = result.Projects,
            };
        }

        /// <summary>The folders and projects directly under one level, optionally with the whole subtree.</summary>
        private static (List<FolderInfo> Folders, List<ProjectInfo> Projects) Level(List<TreeNode> nodes, List<string> parentPath, bool recursive)
        {
            var folders = new List<FolderInfo>();
            var projects = new List<ProjectInfo>();
            foreach (var node in nodes)
            {
                if (node.IsFolder)
                {
                    var path = parentPath.Concat(new[] { node.Name }).ToList();
                    var folder = new FolderInfo
                    {
                        Name = node.Name,
                        Path = string.Join("/", path),
                        FolderCount = node.Children.Count(c => c.IsFolder),
                        ProjectCount = node.Children.Count(c => !c.IsFolder),
                        TotalProjectCount = CountProjects(node),
                    };
                    if (recursive)
                    {
                        var inner = Level(node.Children, path, true);
                        folder.Folders = inner.Folders;
                        folder.Projects = inner.Projects;
                    }

                    folders.Add(folder);
                }
                else
                {
                    projects.Add(new ProjectInfo
                    {
                        Name = node.Project.Name,
                        UniqueName = node.Project.UniqueName,
                        FullName = node.Project.FullName,
                        Kind = node.Project.Kind,
                        IsLoaded = node.Project.IsLoaded,
                    });
                }
            }

            return (folders, projects);
        }

        private static int CountProjects(TreeNode node) =>
            node.IsFolder ? node.Children.Sum(CountProjects) : 1;
    }
}
