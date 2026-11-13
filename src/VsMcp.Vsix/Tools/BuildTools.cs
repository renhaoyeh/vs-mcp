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

namespace VsMcp.Tools
{
    /// <summary>Builds exactly the requested projects and reports the result.</summary>
    internal sealed class BuildTools
    {
        private readonly BuildService service;
        private readonly VsContext serviceContext;
        public BuildTools(VsContext vs) { service = new BuildService(vs); serviceContext = vs; }

        [McpServerTool(Name = "get_build_settings", Title = "Get build settings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Read the connected IDE's persistent build and run preferences: project parallelism, output/log verbosity, startup dependency build policy, out-of-date/build-error launch policies, low-priority builds, native stop-after-first-failure policy and C++ build timing/logging. Includes instance identity and unsupportedSettings reasons; null values are unavailable, not false/defaults. Works without an open solution; support depends on host version and installed workloads. No CPU usage limit is provided.")]
        public Task<BuildSettingsOutput> GetBuildSettingsTool(CancellationToken cancellationToken = default) => new BuildSettingsService(serviceContext).ReadOrUpdateAsync(null, cancellationToken);

        [McpServerTool(Name = "set_build_settings", Title = "Set build settings", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Update only the specified persistent build and run preferences in the connected idle IDE. Supply at least one field. All requested settings must be readable/supported before any write. Returns instance identity, previous snapshot and read-back values. Settings affect subsequent operations across solutions and are not restored automatically; save previous to restore explicitly. No build, launch, active configuration or startup project change is performed. Low priority is an IDE build preference, not an OS process-priority mutation or CPU cap. Updates across settings providers are not transactional. updateStatus pendingApproval means native VS approval is unresolved and C++ timing/logging updates were not applied; inspect/resolve the VS request and read settings before issuing further updates. After errors or interruption, read get_build_settings before retrying.")]
        public Task<BuildSettingsOutput> SetBuildSettingsTool(
            [Description("Positive maximum concurrent project builds; host validates its supported range. Not a compiler-thread or CPU cap."), Range(1, int.MaxValue)] int? maxParallelProjectBuilds = null,
            [Description("MSBuild Output window detail: quiet, minimal, normal, detailed or diagnostic.")] BuildVerbosity? outputVerbosity = null,
            [Description("C++ MSBuild log file detail: quiet, minimal, normal, detailed or diagnostic.")] BuildVerbosity? logFileVerbosity = null,
            [Description("On F5/Ctrl+F5, build only startup projects and their dependencies.")] bool? onlyBuildStartupProjectsAndDependencies = null,
            [Description("C++ out-of-date launch policy: alwaysBuild, neverBuild or promptToBuild.")] OutOfDateBuildBehavior? onRunWhenOutOfDate = null,
            [Description("C++ failed-build launch policy: launchOld, doNotLaunch or promptToLaunch. launchOld can run stale binaries.")] BuildErrorRunBehavior? onRunWhenBuildErrors = null,
            [Description("Run subsequent IDE builds at low process priority; existing processes are not modified directly.")] bool? lowPriorityBuild = null,
            [Description("Native solution-build stop policy on supporting hosts (VS 18.11+). Separate from build_projects.stopOnFirstError.")] bool? stopSolutionBuildAfterFirstFailure = null,
            [Description("Show C++ build tool timings.")] bool? cppBuildTiming = null,
            [Description("Enable C++ build activity log files.")] bool? cppBuildLogging = null,
            CancellationToken cancellationToken = default) => new BuildSettingsService(serviceContext).ReadOrUpdateAsync(new BuildSettingsValues
            {
                MaxParallelProjectBuilds = maxParallelProjectBuilds, OutputVerbosity = outputVerbosity, LogFileVerbosity = logFileVerbosity,
                OnlyBuildStartupProjectsAndDependencies = onlyBuildStartupProjectsAndDependencies, OnRunWhenOutOfDate = onRunWhenOutOfDate,
                OnRunWhenBuildErrors = onRunWhenBuildErrors, LowPriorityBuild = lowPriorityBuild,
                StopSolutionBuildAfterFirstFailure = stopSolutionBuildAfterFirstFailure, CppBuildTiming = cppBuildTiming, CppBuildLogging = cppBuildLogging,
            }, cancellationToken);

        [McpServerTool(Name = "build_projects", Title = "Build projects", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Build, clean or rebuild the given projects in the active solution configuration and platform, as Visual Studio does when you build, and wait for the result. Read the active configuration with get_solution_configurations; to build another one, set it active in Visual Studio first. This tool never changes it. 'operation' is 'build' (default), 'clean' (delete the projects' build output) or 'rebuild' (clean, then build). Clean and rebuild delete build output on disk and nothing else; they never run git. Give either 'projects' (built in the listed order) or 'folder' (every project below that solution folder, ordered so a project comes after the projects it requires within the folder). With 'folder', projects the configuration does not build (shouldBuild false) are listed in 'skipped'. By default the build stops at the first failed project ('stopOnFirstError'); the projects not reached report 'notBuilt' and 'stoppedEarly' is true. The projects the requested ones require, directly or indirectly, are built first, like building a project or a solution folder in Visual Studio; they are listed in build order, and a dependency the configuration does not build is listed in 'skipped'. A clean never adds dependencies. Each project is built with the project configuration that the solution configuration maps it to; the active configuration is never changed. A requested project that the solution configuration does not build (shouldBuild false) is rejected instead of built. 'status' is 'succeeded', 'failed' or 'cancelled' once Visual Studio reports the build finished, and 'running' if it was still going when 'timeoutSeconds' elapsed (the build continues). Per-project 'result' tells which projects built, failed or were not reached. This writes build output to disk. Fails with BUILD_IN_PROGRESS while another build is running. Prefer VS MCP for builds. A missing or unloaded project alone is not a reason to immediately switch to a command-line build. Confirm get_vs_info and get_projects(recursive: true); use project_reload only for a project already in the solution and unloaded. If absent, first determine whether the connected solution is the intended one; switching solutions or adding projects must stay within the user's authorized scope. If the VS MCP path remains blocked after inspection, a command-line build such as MSBuild or dotnet build may be used; explain the blocker and fallback reason. Preserve unrelated running applications. When get_projects reports isLoaded false, call project_reload with that uniqueName, confirm isLoaded true, then continue the requested VS MCP build. An unloaded project alone does not justify fallback.")]
        public async Task<BuildOutput> BuildProjectsTool([Description("Projects to build, by name or unique name, in build order. Their dependencies are built first. Use this or 'folder', not both."), MinLength(1)] List<string> projects = null, [Description("A '/'-separated solution folder path (see get_projects). Builds every project below it, ordered by their dependencies on each other. Use this or 'projects', not both.")] string folder = null, [Description("Cancel the rest of the build as soon as one project fails.")] bool stopOnFirstError = true, [Description("build (default), clean or rebuild.")] BuildOperation operation = BuildOperation.Build, [Description("How long to wait for the build to finish, in seconds. The build continues if it is exceeded."), Range(1, 3600)] int timeoutSeconds = 600, CancellationToken cancellationToken = default)
        {
            return await service.BuildAsync(projects, folder, stopOnFirstError, operation, timeoutSeconds, cancellationToken).ConfigureAwait(true);
        }

        [McpServerTool(Name = "get_build_status", Title = "Get build status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Report the most recent build started by build_projects: 'status' (running, succeeded, failed or cancelled), per-project results, errors and the tail of the Build output. Use it to collect the result of a build that was still 'running' when build_projects returned. The output and errors are read from Visual Studio's Build output and Error List when you call, so they describe the latest build in the IDE. Read-only. Fails with NO_BUILD if no build was started yet.")]
        public async Task<BuildOutput> GetBuildStatusTool(CancellationToken cancellationToken = default)
        {
            return await service.StatusAsync(cancellationToken).ConfigureAwait(true);
        }

        [McpServerTool(Name = "cancel_build", Title = "Cancel the running build", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Ask Visual Studio to cancel the build that is running. Does nothing, and says so, if no build is running. Use get_build_status afterwards to see the final status.")]
        public async Task<CancelBuildOutput> CancelBuildTool(CancellationToken cancellationToken = default)
        {
            return await service.CancelAsync(cancellationToken);
        }

    }
}
