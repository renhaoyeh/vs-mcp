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
        public BuildTools(VsContext vs) { service = new BuildService(vs); }

        [McpServerTool(Name = "build_projects", Title = "Build projects", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Build, clean or rebuild the given projects in the active solution configuration and platform, as Visual Studio does when you build, and wait for the result. Read the active configuration with get_solution_configurations; to build another one, set it active in Visual Studio first. This tool never changes it. 'operation' is 'build' (default), 'clean' (delete the projects' build output) or 'rebuild' (clean, then build). Clean and rebuild delete build output on disk and nothing else; they never run git. Give either 'projects' (built in the listed order) or 'folder' (every project below that solution folder, ordered so a project comes after the projects it requires within the folder). With 'folder', projects the configuration does not build (shouldBuild false) are listed in 'skipped'. By default the build stops at the first failed project ('stopOnFirstError'); the projects not reached report 'notBuilt' and 'stoppedEarly' is true. The projects the requested ones require, directly or indirectly, are built first, like building a project or a solution folder in Visual Studio; they are listed in build order, and a dependency the configuration does not build is listed in 'skipped'. A clean never adds dependencies. Each project is built with the project configuration that the solution configuration maps it to; the active configuration is never changed. A requested project that the solution configuration does not build (shouldBuild false) is rejected instead of built. 'status' is 'succeeded', 'failed' or 'cancelled' once Visual Studio reports the build finished, and 'running' if it was still going when 'timeoutSeconds' elapsed (the build continues). Per-project 'result' tells which projects built, failed or were not reached. This writes build output to disk. Fails with BUILD_IN_PROGRESS while another build is running.")]
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
