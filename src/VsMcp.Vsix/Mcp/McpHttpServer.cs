using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VsMcp.Mcp
{
    /// <summary>
    /// Hosts the official MCP SDK (ModelContextProtocol.Core) behind a loopback-only <see cref="HttpListener"/>.
    /// The SDK handles the protocol (initialize, tools/list, tools/call, versions, errors); this class only
    /// frames HTTP requests, in the same stateless mode the SDK's ASP.NET Core integration offers.
    /// </summary>
    internal sealed class McpHttpServer : IDisposable
    {
        public const int DefaultPort = 18770;
        public const string ServerVersion = "0.4.0";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        private const string Instructions =
            "Controls the Visual Studio instance that hosts this server. Call get_vs_info first and confirm the process ID, version and solution path " +
            "are the intended instance. get_solution_configurations is read-only: it reports the solution's configurations and each project's mapped " +
            "project configuration and platform, and never changes the active configuration. If a filter does not match, the call fails and lists the " +
            "available values; choose one explicitly instead of assuming.";

        private readonly int _port;
        private readonly string _instanceId;
        private readonly IMcpToolProvider _tools;
        private readonly Action<string> _log;
        private readonly HttpListener _listener = new HttpListener();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

        public McpHttpServer(int port, IMcpToolProvider tools, Action<string> log, string instanceId = null)
        {
            _port = port;
            _instanceId = instanceId;
            _tools = tools;
            _log = log ?? (_ => { });
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public void Start()
        {
            _listener.Start();
            _log($"MCP server listening on http://127.0.0.1:{_port}/mcp");
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Close(); } catch { /* already closed */ }
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (token.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log("Accept failed: " + ex.Message);
                    continue;
                }

                _ = Task.Run(() => GuardedHandleAsync(context, token));
            }
        }

        /// <summary>
        /// Outer guard that references no SDK types. If <see cref="HandleAsync"/> cannot even be compiled
        /// (for example a dependency assembly fails to load inside Visual Studio) the failure surfaces here
        /// instead of vanishing in an unobserved task and leaving the client waiting.
        /// </summary>
        private async Task GuardedHandleAsync(HttpListenerContext context, CancellationToken token)
        {
            try
            {
                await HandleAsync(context, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log("Request handler failed before it could answer: " + ex);
                try { Respond(context, HttpStatusCode.InternalServerError, "MCP request handler failed: " + ex.GetType().Name + ": " + ex.Message); } catch { /* connection gone */ }
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
        {
            try
            {
                var request = context.Request;
                var expectedInstance = request.Headers["X-VsMcp-Instance"];
                if (expectedInstance != null && expectedInstance != _instanceId)
                {
                    Respond(context, HttpStatusCode.Conflict, "The selected Visual Studio instance has changed.");
                    return;
                }
                if (!IsLoopbackHost(request.Headers["Host"]) || !IsAllowedOrigin(request.Headers["Origin"]))
                {
                    Respond(context, HttpStatusCode.Forbidden, "Forbidden");
                    return;
                }

                if (request.Url.AbsolutePath.TrimEnd('/') != "/mcp")
                {
                    Respond(context, HttpStatusCode.NotFound, "Not found");
                    return;
                }

                if (request.HttpMethod != "POST")
                {
                    // Stateless: no server-initiated stream (GET) and no sessions to end (DELETE).
                    context.Response.AddHeader("Allow", "POST");
                    Respond(context, HttpStatusCode.MethodNotAllowed, "Only POST is supported");
                    return;
                }

                string body;
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                JsonRpcMessage message;
                try
                {
                    message = System.Text.Json.JsonSerializer.Deserialize(body, McpJsonUtilities.DefaultOptions.GetTypeInfo<JsonRpcMessage>());
                }
                catch (System.Text.Json.JsonException ex)
                {
                    RespondJsonRpcError(context, HttpStatusCode.BadRequest, McpErrorCode.InvalidRequest, "Bad Request: not a valid JSON-RPC message: " + ex.Message);
                    return;
                }

                if (message == null)
                {
                    RespondJsonRpcError(context, HttpStatusCode.BadRequest, McpErrorCode.InvalidRequest, "Bad Request: not a valid JSON-RPC message.");
                    return;
                }

                var protocolVersion = request.Headers["MCP-Protocol-Version"];
                if (!string.IsNullOrEmpty(protocolVersion))
                {
                    message.Context = message.Context ?? new JsonRpcMessageContext();
                    message.Context.ProtocolVersion = protocolVersion;
                }

                // Each request gets its own transport and server, as in the SDK's stateless HTTP mode.
                var transport = new StreamableHttpServerTransport(_loggerFactory) { Stateless = true };
                var server = McpServer.Create(transport, CreateServerOptions(), _loggerFactory, new EmptyServiceProvider());
                var serverRun = server.RunAsync(token);
                var buffer = new MemoryStream();
                try
                {
                    // Never wait forever: if the SDK's server loop dies (for example on an assembly version
                    // mismatch inside Visual Studio) no response will ever be produced, so report that instead.
                    var post = transport.HandlePostRequestAsync(message, buffer, token);
                    var finished = await Task.WhenAny(post, serverRun, Task.Delay(RequestTimeout, token)).ConfigureAwait(false);
                    if (finished != post)
                    {
                        var reason = finished == serverRun
                            ? "MCP server loop ended before answering: " + (serverRun.Exception?.GetBaseException().ToString() ?? "no error reported")
                            : "MCP request timed out after " + RequestTimeout.TotalSeconds + " s";
                        _log(reason);
                        RespondJsonRpcError(context, HttpStatusCode.InternalServerError, McpErrorCode.InternalError, reason);
                        return;
                    }

                    var wroteResponse = await post.ConfigureAwait(false);
                    if (!wroteResponse || buffer.Length == 0)
                    {
                        Respond(context, HttpStatusCode.Accepted, null);
                    }
                    else
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.OK;
                        context.Response.ContentType = "text/event-stream";
                        context.Response.AddHeader("Cache-Control", "no-cache");
                        context.Response.ContentLength64 = buffer.Length;
                        buffer.Position = 0;
                        await buffer.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
                        context.Response.Close();
                    }
                }
                finally
                {
                    try
                    {
                        await server.DisposeAsync().ConfigureAwait(false);
                        await transport.DisposeAsync().ConfigureAwait(false);
                        await Task.WhenAny(serverRun, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log("Request cleanup failed: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _log("Request failed: " + ex);
                try { Respond(context, HttpStatusCode.InternalServerError, "Internal error"); } catch { /* connection gone */ }
            }
        }

        private McpServerOptions CreateServerOptions() => new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "vs-mcp", Title = "Visual Studio MCP", Version = ServerVersion },
            ServerInstructions = Instructions,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
            ScopeRequests = false,
            ToolCollection = CreateToolCollection(),
        };

        private McpServerPrimitiveCollection<McpServerTool> CreateToolCollection()
        {
            var collection = new McpServerPrimitiveCollection<McpServerTool>();
            foreach (var tool in _tools.Tools) collection.Add(tool);
            return collection;
        }

        private static void RespondJsonRpcError(HttpListenerContext context, HttpStatusCode status, McpErrorCode code, string message)
        {
            var body = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = null,
                ["error"] = new JObject { ["code"] = (int)code, ["message"] = message },
            };
            var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            var name = host.Contains(":") ? host.Substring(0, host.LastIndexOf(':')) : host;
            return name == "127.0.0.1" || name == "localhost";
        }

        private static bool IsAllowedOrigin(string origin)
        {
            // Non-browser MCP clients send no Origin. A browser page must come from loopback.
            if (string.IsNullOrEmpty(origin)) return true;
            return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.Host == "127.0.0.1" || uri.Host == "localhost");
        }

        private static void Respond(HttpListenerContext context, HttpStatusCode status, string text)
        {
            context.Response.StatusCode = (int)status;
            if (text != null)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            context.Response.Close();
        }

        private sealed class EmptyServiceProvider : IServiceProvider
        {
            public object GetService(Type serviceType) => null;
        }
    }
}
