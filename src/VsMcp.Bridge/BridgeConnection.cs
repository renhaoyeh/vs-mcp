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
    private InstanceRegistration? bound;
    private bool binding;
    private int activeCalls;

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

    private static CallToolResult Failure(string error, string message, InstanceRegistration? entry, bool outcomeUnknown)
    {
        var content = JsonSerializer.SerializeToElement(new { error, message, instance = entry, outcomeUnknown }, Json);
        return new CallToolResult { IsError = true, StructuredContent = content, Content = [new TextContentBlock { Text = content.GetRawText() }] };
    }
}

internal sealed record DiscoveredInstance(int Pid, string InstanceId, string Endpoint, InstanceInfo? Instance, string? Error);
internal sealed record DiscoveryResult(int? BoundPid, string? BoundInstanceId, DiscoveredInstance[] Instances);
internal sealed record BindingResult(InstanceInfo Instance, string InstanceId, string Endpoint);
