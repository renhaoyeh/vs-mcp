using System;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsMcp.Mcp;
using VsMcp.Tools;
using Task = System.Threading.Tasks.Task;

namespace VsMcp
{
    /// <summary>
    /// Starts the loopback MCP server when Visual Studio loads the extension.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class VsMcpPackage : AsyncPackage
    {
        public const string PackageGuidString = "d806931e-843a-4254-ac50-9f9e48117564";

        private McpHttpServer _server;
        private VsToolProvider _tools;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var dte = await GetServiceAsync(typeof(SDTE)) as DTE2;
            if (dte == null)
            {
                ActivityLog.LogError(nameof(VsMcpPackage), "DTE service is unavailable; the MCP server was not started.");
                return;
            }

            var tools = _tools = new VsToolProvider(dte, JoinableTaskFactory);
            // The port is fixed at 18770 unless VS_MCP_PORT names another one, so a second instance can be given its own.
            var port = int.TryParse(Environment.GetEnvironmentVariable("VS_MCP_PORT"), out var configured) && configured > 0 && configured < 65536
                ? configured
                : McpHttpServer.DefaultPort;
            _server = new McpHttpServer(port, tools, message => ActivityLog.LogInformation(nameof(VsMcpPackage), message));
            try
            {
                _server.Start();
            }
            catch (Exception ex)
            {
                // Most commonly the port is already held by another Visual Studio instance.
                // Report it and leave the other instance as the only endpoint; never reroute.
                ActivityLog.LogError(nameof(VsMcpPackage), "The MCP server could not start: " + ex.Message);
                _server.Dispose();
                _server = null;
                _tools?.Dispose();
                _tools = null;

                // Do not fail silently: an agent would otherwise reach the other instance's endpoint unnoticed.
                VsShellUtilities.ShowMessageBox(
                    this,
                    $"This Visual Studio instance has no MCP endpoint.\n\nPort {port} could not be opened (most likely another Visual Studio instance already serves it).\nAgents connected to that port are talking to the other instance. Set the VS_MCP_PORT environment variable to give this instance its own port.\n\nDetail: {ex.Message}",
                    "VS MCP",
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_WARNING,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tools?.Dispose();
                _tools = null;
                _server?.Dispose();
                _server = null;
            }

            base.Dispose(disposing);
        }
    }
}
