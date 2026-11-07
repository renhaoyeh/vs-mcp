using VsMcp.Services.Build;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VsMcp.Contracts;
using Task = System.Threading.Tasks.Task;

namespace VsMcp.Services.Build
{
    /// <summary>One project to build, with the exact project configuration and platform to build it in.</summary>
    internal sealed class BuildTarget
    {
        public string UniqueName;
        public string ProjectConfiguration;
        public string ProjectPlatform;
    }

    internal sealed class BuildProjectResult
    {
        public string UniqueName;
        public bool Succeeded;
        public bool Cancelled;
    }

    /// <summary>What Visual Studio has reported so far about one build.</summary>
    internal sealed class BuildOutcome
    {
        /// <summary>True once Visual Studio reported the whole build as finished.</summary>
        public bool Finished;
        public bool Succeeded;
        public bool Cancelled;
        public List<BuildProjectResult> Projects = new List<BuildProjectResult>();
    }

    /// <summary>A build request and its progress. The latest session stays available after the build ends.</summary>
    internal sealed class BuildSession
    {
        public string SolutionConfiguration;
        public string SolutionPlatform;
        public List<BuildTarget> Targets = new List<BuildTarget>();

        /// <summary>One entry per target: project, unique name and the project configuration and platform it builds in.</summary>
        public List<BuildProjectOutput> Mapped = new List<BuildProjectOutput>();

        /// <summary>Projects of a folder build that were left out because the configuration does not build them.</summary>
        public List<SkippedProject> Skipped = new List<SkippedProject>();

        /// <summary>Cancel the rest of the build once one project fails.</summary>
        public bool StopOnFirstError;

        public BuildOperation Operation;

        /// <summary>True when this server cancelled the build because a project failed.</summary>
        public bool StoppedEarly;

        public BuildOutcome Outcome = new BuildOutcome();
        public DateTime StartedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Builds exactly the listed projects, in the listed order, using Visual Studio's own build manager
    /// (<see cref="IVsSolutionBuildManager2.StartUpdateSpecificProjectConfigurations"/>). Dependent projects are not
    /// added automatically and the active solution configuration is not changed. Visual Studio's completion event
    /// finishes the session, so a build that outlives the caller's wait is still cleaned up.
    /// </summary>
    internal sealed class BuildRunner : IVsUpdateSolutionEvents2
    {
        private static int s_running;

        private readonly JoinableTaskFactory _joinableTaskFactory;
        private readonly BuildSession _session;
        private readonly TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private IVsSolution _solution;
        private IVsSolutionBuildManager2 _manager;
        private uint _cookie;

        public BuildRunner(JoinableTaskFactory joinableTaskFactory, BuildSession session)
        {
            _joinableTaskFactory = joinableTaskFactory;
            _session = session;
        }

        /// <summary>The most recent build started by this server, finished or not.</summary>
        public static BuildSession Last { get; private set; }

        /// <summary>True from the start of a build started by this server until Visual Studio reports it finished.</summary>
        public static bool IsRunning => Volatile.Read(ref s_running) != 0;

        /// <summary>True when Visual Studio itself is building, cleaning or deploying. UI thread only.</summary>
        public static bool IsBuildManagerBusy()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var manager = (IVsSolutionBuildManager)Package.GetGlobalService(typeof(SVsSolutionBuildManager));
            ErrorHandler.ThrowOnFailure(manager.QueryBuildManagerBusy(out var busy));
            return busy != 0;
        }

        /// <summary>Asks Visual Studio to cancel the running build. UI thread only.</summary>
        public static void RequestCancel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var manager = (IVsSolutionBuildManager2)Package.GetGlobalService(typeof(SVsSolutionBuildManager));
            ErrorHandler.ThrowOnFailure(manager.CancelUpdateSolutionConfiguration());
        }

        /// <summary>Starts the build and waits up to <paramref name="timeout"/> for Visual Studio to finish it.</summary>
        public async Task<BuildOutcome> RunAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (Interlocked.CompareExchange(ref s_running, 1, 0) != 0)
            {
                throw new InvalidOperationException("A build started by this server is still running.");
            }

            Last = _session;
            try
            {
                _manager = (IVsSolutionBuildManager2)Package.GetGlobalService(typeof(SVsSolutionBuildManager));
                _solution = (IVsSolution)Package.GetGlobalService(typeof(SVsSolution));

                var count = _session.Targets.Count;
                var hierarchies = new IVsHierarchy[count];
                var configurations = new IVsCfg[count];
                for (var i = 0; i < count; i++)
                {
                    configurations[i] = GetProjectConfiguration(_session.Targets[i], out hierarchies[i]);
                }

                ErrorHandler.ThrowOnFailure(_manager.AdviseUpdateSolutionEvents(this, out _cookie));
                ErrorHandler.ThrowOnFailure(_manager.StartUpdateSpecificProjectConfigurations(
                    (uint)count,
                    hierarchies,
                    configurations,
                    null,
                    null,
                    null,
                    OperationFlags(_session.Operation),
                    1));
            }
            catch
            {
                Cleanup();
                throw;
            }

            // Wait without blocking the UI thread. A timeout is not a failure: the build keeps running and
            // Visual Studio's completion event still ends the session.
            await Task.WhenAny(_done.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested && !_done.Task.IsCompleted)
            {
                RequestCancel();
            }

            return _session.Outcome;
        }

        private static uint OperationFlags(BuildOperation operation)
        {
            switch (operation)
            {
                case BuildOperation.Clean: return (uint)VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_CLEAN;
                case BuildOperation.Rebuild: return (uint)(VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_CLEAN | VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_BUILD);
                default: return (uint)VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_BUILD;
            }
        }

        private IVsCfg GetProjectConfiguration(BuildTarget target, out IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ErrorHandler.ThrowOnFailure(_solution.GetProjectOfUniqueName(target.UniqueName, out hierarchy));

            // Managed projects expose a configuration provider on the hierarchy.
            if (hierarchy is IVsGetCfgProvider getProvider
                && ErrorHandler.Succeeded(getProvider.GetCfgProvider(out var provider))
                && provider is IVsCfgProvider2 provider2
                && ErrorHandler.Succeeded(provider2.GetCfgOfName(target.ProjectConfiguration, target.ProjectPlatform, out var configuration))
                && configuration != null)
            {
                return configuration;
            }

            // C++ (vcxproj) projects do not. Take the project's current configuration object only to reach its
            // configuration provider (nothing is changed), then open the wanted configuration by its canonical
            // "configuration|platform" name.
            var canonical = target.ProjectConfiguration + "|" + target.ProjectPlatform;
            var current = new IVsProjectCfg[1];
            if (ErrorHandler.Succeeded(_manager.FindActiveProjectCfg(IntPtr.Zero, IntPtr.Zero, hierarchy, current))
                && current[0] != null
                && ErrorHandler.Succeeded(current[0].get_ProjectCfgProvider(out var projectProvider))
                && projectProvider != null
                && ErrorHandler.Succeeded(projectProvider.OpenProjectCfg(canonical, out var projectConfiguration))
                && projectConfiguration != null)
            {
                return projectConfiguration;
            }

            throw new InvalidOperationException($"Project '{target.UniqueName}' has no project configuration '{canonical}' that Visual Studio can open.");
        }

        private string UniqueNameOf(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ErrorHandler.Succeeded(_solution.GetUniqueNameOfProject(hierarchy, out var name)) ? name : null;
        }

        /// <summary>Stops listening and releases the "a build is running" guard. UI thread only.</summary>
        private void Cleanup()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_cookie != 0 && _manager != null)
            {
                _manager.UnadviseUpdateSolutionEvents(_cookie);
                _cookie = 0;
            }

            Interlocked.Exchange(ref s_running, 0);
        }

        public int UpdateProjectCfg_Begin(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, ref int pfCancel) => VSConstants.S_OK;

        public int UpdateProjectCfg_Done(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, int fSuccess, int fCancel)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // A rebuild may report each project twice (clean, then build): keep one result that is only successful if both were.
            var uniqueName = UniqueNameOf(pHierProj);
            var previous = _session.Outcome.Projects.FirstOrDefault(p => string.Equals(p.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase));
            if (previous == null)
            {
                _session.Outcome.Projects.Add(new BuildProjectResult { UniqueName = uniqueName, Succeeded = fSuccess != 0, Cancelled = fCancel != 0 });
            }
            else
            {
                previous.Succeeded = previous.Succeeded && fSuccess != 0;
                previous.Cancelled = previous.Cancelled || fCancel != 0;
            }

            // A project failed on its own (not because of a cancel): stop the rest of the build.
            if (_session.StopOnFirstError && fSuccess == 0 && fCancel == 0 && !_session.StoppedEarly)
            {
                _session.StoppedEarly = true;
                _manager.CancelUpdateSolutionConfiguration();
            }

            return VSConstants.S_OK;
        }

        public int UpdateSolution_Begin(ref int pfCancelUpdate) => VSConstants.S_OK;

        public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
        {
            _session.Outcome.Succeeded = fSucceeded != 0;
            // A cancel that this server issued after a failure is reported as the failure it was.
            _session.Outcome.Cancelled = fCancelCommand != 0 && !_session.StoppedEarly;
            _session.Outcome.Finished = true;
            _done.TrySetResult(true);

            // Do not unadvise from inside the event that is being delivered; finish just after it returns.
            _joinableTaskFactory.RunAsync(async () =>
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                Cleanup();
            }).Task.FileAndForget("vsmcp/build-cleanup");
            return VSConstants.S_OK;
        }

        public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;

        public int UpdateSolution_Cancel() => VSConstants.S_OK;

        public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;
    }
}
