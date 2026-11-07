using VsMcp.Services.VisualStudio;
using VsMcp.Services.Build;
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

namespace VsMcp.Services.Build
{
    /// <summary>Builds exactly the requested projects and reports the result.</summary>
    internal sealed class BuildService
    {
        private readonly VsContext _vs;
        private readonly DTE2 _dte;
        private readonly JoinableTaskFactory _joinableTaskFactory;

        public BuildService(VsContext vs)
        {
            _vs = vs;
            _dte = vs.Dte;
            _joinableTaskFactory = vs.JoinableTaskFactory;
        }

        public async Task<BuildOutput> BuildAsync([Description("Projects to build, by name or unique name, in build order. Their dependencies are built first. Use this or 'folder', not both."), MinLength(1)] List<string> projects = null, [Description("A '/'-separated solution folder path (see get_projects). Builds every project below it, ordered by their dependencies on each other. Use this or 'projects', not both.")] string folder = null, [Description("Cancel the rest of the build as soon as one project fails.")] bool stopOnFirstError = true, [Description("build (default), clean or rebuild.")] BuildOperation operation = BuildOperation.Build, [Description("How long to wait for the build to finish, in seconds. The build continues if it is exceeded."), Range(1, 3600)] int timeoutSeconds = 600, CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return await BuildProjectsAsync(projects, folder, stopOnFirstError, operation, timeoutSeconds, cancellationToken).ConfigureAwait(true);
        }

        public async Task<BuildOutput> StatusAsync(CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return await GetBuildStatusAsync(cancellationToken).ConfigureAwait(true);
        }

        public async Task<CancelBuildOutput> CancelAsync(CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return CancelBuild();
        }

        private const int MaxBuildOutputCharacters = 20000;

        private async Task<BuildOutput> BuildProjectsAsync(List<string> requested, string folderPath, bool stopOnFirstError, BuildOperation operation, int timeoutSeconds, CancellationToken cancellationToken)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var instance = _vs.Instance();
            if (_vs.TestsRunning()) throw new McpToolException(ToolErrorCode.ExecutionError, "Wait for or cancel the owned test run before building.");
            if (_vs.DebugLaunchPending)
                throw new McpToolException(ToolErrorCode.DebugLaunchPending, "A debug launch is pending; check get_debug_status before building.");
            if (_dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
                throw new McpToolException(ToolErrorCode.DebuggerActive, "Stop debugging before building projects.");
            if (!_dte.Solution.IsOpen)
            {
                throw new McpToolException(ToolErrorCode.NoSolution, "No solution is open in this Visual Studio instance.", new JObject { ["instance"] = instance });
            }

            folderPath = (folderPath ?? "").Trim().Trim('/');
            var fromFolder = folderPath.Length > 0;
            var hasProjects = requested != null && requested.Count > 0;
            if (fromFolder == hasProjects || (hasProjects && requested.Any(string.IsNullOrWhiteSpace)))
            {
                throw new McpToolException(ToolErrorCode.InvalidArgument, "Give exactly one of 'projects' (a non-empty list) or 'folder'.", new JObject { ["instance"] = instance });
            }

            // Always the active configuration: the caller reads it with get_solution_configurations and sets it in Visual Studio.
            var active = _dte.Solution.SolutionBuild.ActiveConfiguration as SolutionConfiguration2;
            if (active == null)
            {
                throw new McpToolException(ToolErrorCode.NoActiveConfiguration, "The solution has no active configuration.", new JObject { ["instance"] = instance });
            }

            var wantedConfiguration = active.Name;
            var wantedPlatform = active.PlatformName;

            timeoutSeconds = Math.Max(1, Math.Min(3600, timeoutSeconds));

            var projects = _vs.EnumerateProjects();
            var uniqueNames = new List<string>();
            if (fromFolder)
            {
                // Every project below the folder, ordered so a project follows the projects it requires inside the folder.
                var folder = _vs.ResolveFolder(folderPath, instance, out _);
                var inFolder = new List<ProjectEntry>();
                VsContext.Flatten(folder.Children, inFolder);
                uniqueNames = _vs.OrderByDependencies(inFolder);
                if (uniqueNames.Count == 0)
                {
                    throw new McpToolException(ToolErrorCode.InvalidArgument, $"Solution folder '{folderPath}' contains no projects.", new JObject { ["instance"] = instance });
                }
            }
            else
            {
                // Resolve every project exactly, keeping the caller's order and refusing repeats.
                foreach (var name in requested)
                {
                    var unique = VsContext.ResolveProject(projects, name, instance);
                    if (uniqueNames.Contains(unique, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new McpToolException(ToolErrorCode.DuplicateProject, $"Project '{unique}' is listed more than once.", new JObject { ["instance"] = instance });
                    }

                    uniqueNames.Add(unique);
                }
            }

            // Visual Studio builds what a project requires before it; so do we, unless asked for exactly the listed projects.
            var requestedNames = new HashSet<string>(uniqueNames, StringComparer.OrdinalIgnoreCase);
            var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (operation != BuildOperation.Clean)
            {
                uniqueNames = _vs.OrderWithDependencies(uniqueNames);
                foreach (var name in uniqueNames)
                {
                    if (!requestedNames.Contains(name)) addedNames.Add(name);
                }
            }

            string NameOf(string unique) => projects.FirstOrDefault(p => VsContext.Same(p.UniqueName, unique))?.Name ?? System.IO.Path.GetFileNameWithoutExtension(unique);

            // Find the exact solution configuration and platform, and each project's mapped project configuration.
            SolutionConfiguration2 solutionConfiguration = null;
            var available = new List<string>();
            foreach (SolutionConfiguration2 candidate in _dte.Solution.SolutionBuild.SolutionConfigurations)
            {
                available.Add(candidate.Name + " | " + candidate.PlatformName);
                if (VsContext.Same(candidate.Name, wantedConfiguration) && VsContext.Same(candidate.PlatformName, wantedPlatform)) solutionConfiguration = candidate;
            }

            if (solutionConfiguration == null)
            {
                throw new McpToolException(ToolErrorCode.ConfigurationNotFound, $"No solution configuration '{wantedConfiguration} | {wantedPlatform}'.",
                    new JObject { ["instance"] = instance, ["available_configurations"] = new JArray(available) });
            }

            var targets = new List<BuildTarget>();
            var mapped = new List<BuildProjectOutput>();
            var skipped = new List<SkippedProject>();
            foreach (var unique in uniqueNames)
            {
                SolutionContext context = null;
                foreach (SolutionContext candidate in solutionConfiguration.SolutionContexts)
                {
                    if (VsContext.Same(candidate.ProjectName, unique)) context = candidate;
                }

                if (context == null || !context.ShouldBuild)
                {
                    if (fromFolder || addedNames.Contains(unique))
                    {
                        skipped.Add(new SkippedProject
                        {
                            Project = NameOf(unique),
                            UniqueName = unique,
                            Reason = "not built in this solution configuration (shouldBuild is false)",
                        });
                        continue;
                    }

                    throw new McpToolException(ToolErrorCode.ProjectNotBuiltInConfiguration,
                        $"Project '{unique}' is not built in solution configuration '{wantedConfiguration} | {wantedPlatform}' (shouldBuild is false), so it was not built.",
                        new JObject { ["instance"] = instance });
                }

                targets.Add(new BuildTarget { UniqueName = unique, ProjectConfiguration = context.ConfigurationName, ProjectPlatform = context.PlatformName });
                mapped.Add(new BuildProjectOutput
                {
                    Project = NameOf(unique),
                    UniqueName = unique,
                    ProjectConfiguration = context.ConfigurationName,
                    ProjectPlatform = context.PlatformName,
                });
            }

            if (targets.Count == 0)
            {
                throw new McpToolException(ToolErrorCode.InvalidArgument, $"No project in folder '{folderPath}' is built in solution configuration '{wantedConfiguration} | {wantedPlatform}'. Nothing was started.",
                    new JObject { ["instance"] = instance, ["skipped"] = new JArray(skipped.Select(ToolJson.ToJObject)) });
            }

            if (BuildRunner.IsRunning || BuildRunner.IsBuildManagerBusy())
            {
                throw new McpToolException(ToolErrorCode.BuildInProgress, "Visual Studio is already building, cleaning or deploying. Nothing was started.", new JObject { ["instance"] = instance });
            }

            var session = new BuildSession
            {
                SolutionConfiguration = solutionConfiguration.Name,
                SolutionPlatform = solutionConfiguration.PlatformName,
                Targets = targets,
                Mapped = mapped,
                Skipped = skipped,
                StopOnFirstError = stopOnFirstError,
                Operation = operation,
            };

            try
            {
                await new BuildRunner(_joinableTaskFactory, session).RunAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken).ConfigureAwait(true);
            }
            catch (InvalidOperationException ex)
            {
                throw new McpToolException(ToolErrorCode.BuildNotStarted, ex.Message, new JObject { ["instance"] = instance });
            }

            return await BuildResultAsync(instance, session, cancellationToken).ConfigureAwait(true);
        }

        private async Task<BuildOutput> GetBuildStatusAsync(CancellationToken cancellationToken)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var instance = _vs.Instance();
            var session = BuildRunner.Last;
            if (session == null)
            {
                throw new McpToolException(ToolErrorCode.NoBuild, "No build has been started by this server yet.", new JObject { ["instance"] = instance });
            }

            return await BuildResultAsync(instance, session, cancellationToken).ConfigureAwait(true);
        }

        private CancelBuildOutput CancelBuild()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var running = BuildRunner.IsRunning || BuildRunner.IsBuildManagerBusy();
            if (running) BuildRunner.RequestCancel();
            return new CancelBuildOutput { Instance = _vs.ReadInstance(), CancelRequested = running, Running = BuildRunner.IsRunning };
        }

        /// <summary>The current picture of one build: status, per-project results, output and errors.</summary>
        private async Task<BuildOutput> BuildResultAsync(JObject instance, BuildSession session, CancellationToken cancellationToken)
        {
            // Everything below reads Visual Studio objects; make sure any awaited build left us on the UI thread.
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var outcome = session.Outcome;
            var running = BuildRunner.IsRunning && ReferenceEquals(session, BuildRunner.Last);
            var status = outcome.Finished ? (outcome.Cancelled ? BuildStatus.Cancelled : outcome.Succeeded ? BuildStatus.Succeeded : BuildStatus.Failed)
                : running ? BuildStatus.Running : BuildStatus.Cancelled;

            var projects = new List<BuildProjectOutput>();
            foreach (var entry in session.Mapped)
            {
                var done = outcome.Projects.FirstOrDefault(p => VsContext.Same(p.UniqueName, entry.UniqueName));
                projects.Add(new BuildProjectOutput
                {
                    Project = entry.Project,
                    UniqueName = entry.UniqueName,
                    ProjectConfiguration = entry.ProjectConfiguration,
                    ProjectPlatform = entry.ProjectPlatform,
                    Result = done == null ? (running ? ProjectBuildResult.Running : ProjectBuildResult.NotBuilt)
                        : done.Cancelled ? ProjectBuildResult.Cancelled
                        : done.Succeeded ? ProjectBuildResult.Succeeded : ProjectBuildResult.Failed,
                });
            }

            var errors = new List<BuildError>();
            string output = null;
            var truncated = false;
            if (outcome.Finished)
            {
                output = ReadBuildOutput(out truncated);
                errors = await ReadBuildErrorsAsync(status == BuildStatus.Failed, output, cancellationToken).ConfigureAwait(true);
            }

            return new BuildOutput
            {
                Instance = _vs.ReadInstance(),
                SolutionConfiguration = session.SolutionConfiguration,
                SolutionPlatform = session.SolutionPlatform,
                Operation = session.Operation,
                Status = status,
                Finished = outcome.Finished,
                Running = running,
                Projects = projects,
                ErrorCount = errors.Count,
                Errors = errors,
                StoppedEarly = session.StoppedEarly,
                Skipped = session.Skipped,
                Output = output,
                OutputTruncated = truncated,
            };
        }

        /// <summary>
        /// Errors of a finished build. The Error List is filled slightly after Visual Studio reports the build done, so
        /// when a failed build shows none yet, wait briefly, then fall back to the error lines of the build output.
        /// </summary>
        private async Task<List<BuildError>> ReadBuildErrorsAsync(bool expectErrors, string output, CancellationToken cancellationToken)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var errors = ReadBuildErrors();
            for (var attempt = 0; attempt < 10 && errors.Count == 0 && expectErrors; attempt++)
            {
                await Task.Delay(300, cancellationToken).ConfigureAwait(true);
                errors = ReadBuildErrors();
            }

            if (errors.Count == 0 && expectErrors && output != null)
            {
                var seen = new HashSet<string>();
                foreach (Match match in OutputErrorPattern.Matches(output))
                {
                    var key = match.Groups["file"].Value + "|" + match.Groups["line"].Value + "|" + match.Groups["code"].Value;
                    if (!seen.Add(key) || errors.Count >= 200) continue;
                    errors.Add(new BuildError
                    {
                        Project = match.Groups["proj"].Success ? match.Groups["proj"].Value : null,
                        File = match.Groups["file"].Value.Trim(),
                        Line = int.Parse(match.Groups["line"].Value),
                        Column = match.Groups["col"].Success ? int.Parse(match.Groups["col"].Value) : 0,
                        Description = match.Groups["code"].Value + ": " + match.Groups["msg"].Value.Trim(),
                    });
                }
            }

            return errors;
        }

        private static readonly Regex OutputErrorPattern = new Regex(
            @"^\s*(?:\d+>)?(?<file>.+?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\):\s*error\s+(?<code>[A-Za-z]+\d+):\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$",
            RegexOptions.Multiline);

        /// <summary>The tail of the Build output pane, or null when it cannot be read.</summary>
        private string ReadBuildOutput(out bool truncated)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            truncated = false;
            try
            {
                foreach (OutputWindowPane pane in _dte.ToolWindows.OutputWindow.OutputWindowPanes)
                {
                    if (!string.Equals(pane.Name, "Build", StringComparison.OrdinalIgnoreCase)) continue;
                    var document = pane.TextDocument;
                    var text = document.StartPoint.CreateEditPoint().GetText(document.EndPoint);
                    if (text.Length > MaxBuildOutputCharacters)
                    {
                        truncated = true;
                        text = text.Substring(text.Length - MaxBuildOutputCharacters);
                    }

                    return text;
                }
            }
            catch (Exception)
            {
                // The output window is optional context; the build result does not depend on it.
            }

            return null;
        }

        /// <summary>Errors currently in the Error List (severity "error" only).</summary>
        private List<BuildError> ReadBuildErrors()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<BuildError>();
            try
            {
                var items = _dte.ToolWindows.ErrorList.ErrorItems;
                for (var i = 1; i <= items.Count && result.Count < 200; i++)
                {
                    var item = items.Item(i);
                    if (item.ErrorLevel != vsBuildErrorLevel.vsBuildErrorLevelHigh) continue;
                    result.Add(new BuildError
                    {
                        Project = item.Project,
                        File = item.FileName,
                        Line = item.Line,
                        Column = item.Column,
                        Description = item.Description,
                    });
                }
            }
            catch (Exception)
            {
                // The Error List can lag or be unavailable; the build output still carries the messages.
            }

            return result;
        }
    }
}
