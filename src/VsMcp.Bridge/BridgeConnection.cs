using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using VsMcp.Contracts;
using VsMcp.Infrastructure;

namespace VsMcp.Bridge;

internal sealed class BridgeConnection(InstanceRegistry registry, ToolCatalog catalog)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
    private readonly object sync = new();
    private readonly SemaphoreSlim launchGate = new(1, 1);
    private InstanceRegistration? bound;
    private bool binding;
    private int activeCalls;

    public async Task<CallToolResult> LaunchAsync(string solutionPath, string? devenvPath, string? rootSuffix, bool forceNew, CancellationToken cancellationToken)
    {
        solutionPath = VisualStudioLaunch.ValidateSolutionPath(solutionPath);
        if (rootSuffix != null && (rootSuffix.Length is < 1 or > 32 || !rootSuffix.All(char.IsAsciiLetterOrDigit)))
            throw new McpException("INVALID_ROOT_SUFFIX: Use an alphanumeric experimental hive suffix of 1 to 32 characters.");
        if (devenvPath != null && (!Path.IsPathFullyQualified(devenvPath) || !File.Exists(devenvPath)))
            throw new McpException("INVALID_DEVENV_PATH: Provide an absolute path to an installed Visual Studio 2026 devenv.exe.");

        await launchGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceNew)
            {
                var discovery = await DiscoverAsync(cancellationToken);
                var matches = discovery.Instances.Where(item => item.Instance is { } info && HasSolution(info, solutionPath)).ToArray();
                if (matches.Length == 1 && rootSuffix == null &&
                    (devenvPath == null || string.Equals(matches[0].Instance!.ExecutablePath, Path.GetFullPath(devenvPath), StringComparison.OrdinalIgnoreCase)))
                {
                    var match = matches[0];
                    return Result(new { match.Pid, match.InstanceId, match.Endpoint, match.Instance, reused = true });
                }
                if (matches.Length != 0)
                    return Result(new
                    {
                        error = "VS_SOLUTION_ALREADY_OPEN", solutionPath, matches,
                        message = "The solution is already open. Select an existing PID explicitly, or use forceNew if the user wants another process.",
                    }, isError: true);
            }
            return await StartAndVerifyAsync(solutionPath, devenvPath, rootSuffix, cancellationToken);
        }
        finally { launchGate.Release(); }
    }

    private static bool HasSolution(InstanceInfo info, string solutionPath) =>
        info.SolutionOpen && string.Equals(info.SolutionPath, solutionPath, StringComparison.OrdinalIgnoreCase);

    private async Task<CallToolResult> StartAndVerifyAsync(string solutionPath, string? devenvPath, string? rootSuffix, CancellationToken cancellationToken)
    {
        devenvPath = await VisualStudioLaunch.FindDevenvAsync(devenvPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = VisualStudioLaunch.Start(devenvPath, solutionPath, rootSuffix, out var pid);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            using var process = Process.GetProcessById(pid);
            var startTime = process.StartTime.ToUniversalTime().Ticks;
            while (!process.HasExited)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var entry = registry.ReadAll().FirstOrDefault(entry => entry.Pid == pid && entry.ProcessStartTimeUtcTicks == startTime);
                if (entry != null)
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    probe.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        await using var client = await OpenAsync(entry, probe.Token);
                        var info = await ReadIdentityAsync(client, entry, probe.Token);
                        if (HasSolution(info, solutionPath) && registry.IsCurrent(entry))
                            return Result(new { pid, entry.InstanceId, endpoint = Endpoint(entry), instance = info, reused = false });
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException or McpException or OperationCanceledException)
                    {
                        // Retry only the read-only probe. The process was started exactly once.
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
            }
        }
        catch (ArgumentException) { } // The new process exited before Process.GetProcessById.
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Result(new
            {
                error = "VS_LAUNCH_UNCONFIRMED", pid, solutionPath, devenvPath, rootSuffix, outcomeUnknown = true,
                message = "Visual Studio was started, but the requested solution was not confirmed. Inspect this PID and list_vs_instances before retrying.",
            }, isError: true);
        }
        return Result(new
        {
            error = "VS_LAUNCH_EXITED", pid, solutionPath, devenvPath, rootSuffix, outcomeUnknown = false,
            message = "The new Visual Studio process exited before the requested solution was confirmed.",
        }, isError: true);
    }

    public async Task<DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        InstanceRegistration? selected;
        lock (sync) selected = bound;
        var instances = await Task.WhenAll(registry.ReadAll().Select(async entry =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await using var client = await OpenAsync(entry, timeout.Token);
                var info = await ReadIdentityAsync(client, entry, timeout.Token);
                return new DiscoveredInstance(entry.Pid, entry.InstanceId, Endpoint(entry), info, null);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                return new DiscoveredInstance(entry.Pid, entry.InstanceId, Endpoint(entry), null, error.Message);
            }
        }));
        return new DiscoveryResult(selected?.Pid, selected?.InstanceId, instances);
    }

    public async Task<BindingResult> BindAsync(int pid, string instanceId, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (binding || activeCalls != 0) throw new McpException("VS_BUSY: Wait for active calls before changing the bound instance.");
            binding = true;
        }
        try
        {
            var entry = registry.Find(pid, instanceId);
            if (entry == null || entry.InstanceId != instanceId)
                throw new McpException("VS_INSTANCE_UNAVAILABLE: List instances again and select the current PID and instanceId.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var client = await OpenAsync(entry, timeout.Token);
            var info = await ReadIdentityAsync(client, entry, timeout.Token);
            catalog.Validate((await client.ListToolsAsync(cancellationToken: timeout.Token)).Select(tool => tool.ProtocolTool));
            if (!registry.IsCurrent(entry)) throw new McpException("VS_INSTANCE_CHANGED: List instances again before connecting.");
            lock (sync) bound = entry;
            return new BindingResult(info, entry.InstanceId, Endpoint(entry));
        }
        catch (Exception error) when ((error is HttpRequestException || error is IOException || error is OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            throw new McpException($"VS_CONNECTION_FAILED: Could not bind PID {pid}, instanceId {instanceId}. " + error.Message);
        }
        finally { lock (sync) binding = false; }
    }

    public async ValueTask<CallToolResult> ForwardAsync(CallToolRequestParams request, CancellationToken cancellationToken)
    {
        InstanceRegistration entry;
        lock (sync)
        {
            if (bound == null) return Failure("VS_NOT_CONNECTED", "Use list_vs_instances and connect_vs_instance before calling IDE tools.", null, false);
            if (binding) return Failure("VS_BUSY", "An instance connection is being established.", bound, false);
            entry = bound;
            activeCalls++;
        }
        var dispatched = false;
        try
        {
            if (!registry.IsCurrent(entry))
                return Failure("VS_INSTANCE_UNAVAILABLE", "The bound VS instance is gone or its registration changed. Select an instance explicitly; no other VS was used.", entry, false);
            // Stateless HTTP: each new tool call establishes a fresh connection to the pinned instance.
            // Reconnection never repeats the previous tool call, including when its outcome is unknown.
            using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readyTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var client = await OpenAsync(entry, readyTimeout.Token);
            await ReadIdentityAsync(client, entry, readyTimeout.Token);
            readyTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            dispatched = true;
            return await client.CallToolAsync(request, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException || error is IOException || error is McpException || error is OperationCanceledException)
        {
            return Failure("VS_CONNECTION_FAILED", dispatched
                ? "The request may have taken effect. Inspect the bound VS before retrying; the bridge did not repeat it. " + error.Message
                : "Could not reach the bound VS. A later call can reconnect to this same instance. " + error.Message, entry, dispatched);
        }
        finally { lock (sync) activeCalls--; }
    }

    private static async Task<McpClient> OpenAsync(InstanceRegistration entry, CancellationToken cancellationToken)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Endpoint(entry)), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["X-VsMcp-Instance"] = entry.InstanceId },
        }, http, ownsHttpClient: true);
        try
        {
            return await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(5),
            }, cancellationToken: cancellationToken);
        }
        catch { await transport.DisposeAsync(); throw; }
    }

    private static async Task<InstanceInfo> ReadIdentityAsync(McpClient client, InstanceRegistration entry, CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync("get_vs_info", cancellationToken: cancellationToken);
        if (result.IsError == true || result.StructuredContent is not { } content)
            throw new McpException("VS_IDENTITY_UNAVAILABLE: The endpoint did not return instance identity.");
        var info = content.Deserialize<InstanceInfo>(Json);
        if (info?.Pid != entry.Pid) throw new McpException("VS_IDENTITY_MISMATCH: The endpoint belongs to a different process.");
        return info;
    }

    private static string Endpoint(InstanceRegistration entry) => $"http://127.0.0.1:{entry.Port}/mcp";

    private static CallToolResult Failure(string error, string message, InstanceRegistration? entry, bool outcomeUnknown) =>
        Result(new { error, message, instance = entry, outcomeUnknown }, isError: true);

    private static CallToolResult Result<T>(T value, bool isError = false)
    {
        var content = JsonSerializer.SerializeToElement(value, Json);
        return new CallToolResult { IsError = isError, StructuredContent = content, Content = [new TextContentBlock { Text = content.GetRawText() }] };
    }
}

internal sealed record DiscoveredInstance(int Pid, string InstanceId, string Endpoint, InstanceInfo? Instance, string? Error);
internal sealed record DiscoveryResult(int? BoundPid, string? BoundInstanceId, DiscoveredInstance[] Instances);
internal sealed record BindingResult(InstanceInfo Instance, string InstanceId, string Endpoint);
