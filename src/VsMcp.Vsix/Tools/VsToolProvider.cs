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
using System.IO;
using VsMcp.Infrastructure;
using VsMcp.Services.Documents;
using VsMcp.Services.Navigation;
using VsMcp.Services.Testing;

namespace VsMcp.Tools
{
    /// <summary>The tools of this server: each group is a class of <c>[McpServerTool]</c> methods over a shared <see cref="VsContext"/>.</summary>
    internal sealed class VsToolProvider : IMcpToolProvider, IDisposable
    {
        private readonly TestRunService tests;
        private readonly VsContext context;
        public VsToolProvider(DTE2 dte, JoinableTaskFactory joinableTaskFactory)
        {
            context = new VsContext(dte, joinableTaskFactory);
            // Runner resolution is deferred to execution and is rooted in this host installation.
            var executor = new VstestExecutor(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CommonExtensions", "Microsoft", "TestWindow", "vstest.console.exe"));
            tests = new TestRunService(executor);
            context.TestsRunning = () => tests.IsRunning;
            Tools = ToolJson.CreateTools(new SolutionTools(context), new BuildTools(context), new DebugTools(context), new CodeTools(context),
                new DocumentTools(context, new DocumentService(dte)), new SolutionManagementTools(context), new DiagnosticsTools(context), new DebuggerTools(context), new DebugDiagnosticsTools(context),
                new ProjectWorkflowTools(context),
                new NavigationTools(context, new NavigationService(new RoslynNavigationService(RoslynNavigationService.GetWorkspace), new NativeNavigationService())), new TestTools(context, tests, new TestDiscoveryService(executor)), new ProjectItemsTools(context));
        }

        public IReadOnlyList<McpServerTool> Tools { get; }
        public void Dispose()
        {
            tests.Dispose();
            if (context.DebugEvents != null)
                context.JoinableTaskFactory.Run(async () => { await context.JoinableTaskFactory.SwitchToMainThreadAsync(); context.DebugEvents.Dispose(); });
        }
    }
}
