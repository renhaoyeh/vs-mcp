using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsMcp.Mcp;
using VsMcp.Tools;
using VsMcp.Contracts;
using VsMcp.Infrastructure;
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

        static VsMcpPackage() => HostAssemblyResolver.Register();

        private McpHttpServer _server;
        private VsToolProvider _tools;
        private IDisposable _registration;

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
            var configuredPort = Environment.GetEnvironmentVariable("VS_MCP_PORT");
            var explicitPort = !string.IsNullOrWhiteSpace(configuredPort);
            var port = McpHttpServer.DefaultPort;
            var instanceId = Guid.NewGuid().ToString("N");
            try
            {
                if (explicitPort && (!int.TryParse(configuredPort, out port) || port <= 0 || port > 65535))
                    throw new ArgumentException("VS_MCP_PORT must be between 1 and 65535.");
                for (var attempt = 0; ; attempt++)
                {
                    _server = new McpHttpServer(port, tools, message => ActivityLog.LogInformation(nameof(VsMcpPackage), message), instanceId);
                    try { _server.Start(); break; }
                    catch (HttpListenerException) when (!explicitPort && attempt < 4)
                    {
                        _server.Dispose();
                        var listener = new TcpListener(IPAddress.Loopback, 0);
                        listener.Start();
                        try { port = ((IPEndPoint)listener.LocalEndpoint).Port; }
                        finally { listener.Stop(); }
                    }
                }
                using (var process = Process.GetCurrentProcess())
                    _registration = new InstanceRegistry().Publish(new InstanceRegistration
                    {
                        Pid = process.Id, ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                        Port = port, InstanceId = instanceId,
                    });
                try
                {
                    BridgeDeployment.Install(Path.Combine(Path.GetDirectoryName(typeof(VsMcpPackage).Assembly.Location), "Bridge"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VsMcp", "Bridge"));
                }
                catch (Exception error)
                {
                    ActivityLog.LogError(nameof(VsMcpPackage), "Bridge deployment failed. Stop bridge clients and restart VS to update it: " + error.Message);
                }
            }
            catch (Exception ex)
            {
                ActivityLog.LogError(nameof(VsMcpPackage), "The MCP server could not start: " + ex.Message);
                _registration?.Dispose();
                _registration = null;
                _server?.Dispose();
                _server = null;
                _tools?.Dispose();
                _tools = null;

                // Do not fail silently: an agent would otherwise reach the other instance's endpoint unnoticed.
                VsShellUtilities.ShowMessageBox(
                    this,
                    $"This Visual Studio instance has no MCP endpoint.\n\nPort {port} could not be started or registered. If VS_MCP_PORT is set, give each instance a different explicit port.\n\nDetail: {ex.Message}",
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
                _registration?.Dispose();
                _registration = null;
                _tools?.Dispose();
                _tools = null;
                _server?.Dispose();
                _server = null;
            }

            base.Dispose(disposing);
        }
    }
}
