#nullable enable
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VsMcp.Bridge;
using VsMcp.Contracts;
using VsMcp.Infrastructure;
using VsMcp.Mcp;
using Xunit;

namespace VsMcp.BridgeTests;

public sealed class BridgeTests
{
    [Fact]
    public async Task Stdio_exposes_the_stable_catalog_and_requires_explicit_binding()
    {
        var executable = Path.GetFullPath(typeof(BridgeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "BridgeExecutable").Value!);
        var hostTrace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = executable,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["DOTNET_ROOT"] = Path.Combine(Path.GetTempPath(), "missing-dotnet-" + Guid.NewGuid().ToString("N")),
                ["DOTNET_ROOT_X64"] = Path.Combine(Path.GetTempPath(), "missing-dotnet-" + Guid.NewGuid().ToString("N")),
                ["DOTNET_HOST_TRACE"] = "1",
            },
            StandardErrorLines = hostTrace.Enqueue,
        }));
        var tools = await client.ListToolsAsync();
        Assert.Contains(hostTrace, line => line.Contains(Path.Combine(Path.GetDirectoryName(executable)!, "coreclr.dll"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(55, tools.Count);
        Assert.Contains(tools, tool => tool.Name == "show_vs_window");
        Assert.Contains(tools, tool => tool.Name == "project_unload");
        Assert.Contains(tools, tool => tool.Name == "project_reload");
        Assert.Contains(tools, tool => tool.Name == "start_without_debugging");
        Assert.Contains(tools, tool => tool.Name == "without_debugging_reconcile");
        Assert.False(tools.Single(t => t.Name == "execute_code").ProtocolTool.Annotations!.IdempotentHint);
        Assert.True(tools.Single(t => t.Name == "debugger_list_threads").ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.False(tools.Single(t => t.Name == "debugger_select_frame").ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.Contains(tools, tool => tool.Name == "list_vs_instances");
        Assert.Contains(tools, tool => tool.Name == "connect_vs_instance");
        var result = await client.CallToolAsync("get_vs_info");
        Assert.True(result.IsError);
        Assert.Equal("VS_NOT_CONNECTED", Error(result));
    }

    [Fact]
    public async Task No_VS_is_a_valid_discovery_result()
    {
        using var environment = new TestEnvironment();
        var connection = new BridgeConnection(environment.Registry, ToolCatalog.Load());
        Assert.Empty((await connection.DiscoverAsync(default)).Instances);
    }

    [Fact]
    public async Task Multiple_instances_require_selection_and_switch_only_when_explicitly_bound()
    {
        using var environment = new TestEnvironment();
        using var first = environment.Start(101);
        using var second = environment.Start(102);
        var connection = new BridgeConnection(environment.Registry, first.Catalog);
        var discovery = await connection.DiscoverAsync(default);
        Assert.Equal(2, discovery.Instances.Length);
        Assert.All(discovery.Instances, instance => Assert.Null(instance.Error));
        Assert.Null(discovery.BoundPid);
        Assert.Equal("VS_NOT_CONNECTED", Error(await Call(connection, "mutate")));
        await connection.BindAsync(101, first.Entry.InstanceId, default);
        var result = await Call(connection, "mutate");
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(101, result.StructuredContent!.Value.GetProperty("instance").GetProperty("pid").GetInt32());
        Assert.Equal(1, first.Tools.Mutations);
        Assert.Equal(0, second.Tools.Mutations);
        await connection.BindAsync(102, second.Entry.InstanceId, default);
        await Call(connection, "mutate");
        Assert.Equal(1, first.Tools.Mutations);
        Assert.Equal(1, second.Tools.Mutations);
    }

    [Fact]
    public async Task Lost_registration_does_not_fall_back_to_another_instance_or_reused_PID()
    {
        using var environment = new TestEnvironment();
        using var first = environment.Start(103);
        using var other = environment.Start(104);
        var connection = new BridgeConnection(environment.Registry, first.Catalog);
        await connection.BindAsync(103, first.Entry.InstanceId, default);
        first.RemoveRegistration();
        using var replacement = environment.Start(103);
        Assert.Equal("VS_INSTANCE_UNAVAILABLE", Error(await Call(connection, "mutate")));
        Assert.Equal(0, other.Tools.Mutations);
        Assert.Equal(0, replacement.Tools.Mutations);
        await Assert.ThrowsAsync<McpException>(() => connection.BindAsync(103, first.Entry.InstanceId, default));
    }

    [Fact]
    public async Task A_different_server_on_the_same_port_is_rejected_before_any_tool_runs()
    {
        using var environment = new TestEnvironment();
        using var host = environment.Start(105);
        var connection = new BridgeConnection(environment.Registry, host.Catalog);
        await connection.BindAsync(105, host.Entry.InstanceId, default);
        host.Restart(Guid.NewGuid().ToString("N"));
        var result = await Call(connection, "mutate");
        Assert.True(result.IsError);
        Assert.False(result.StructuredContent!.Value.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Equal(0, host.Tools.Mutations);
    }

    [Fact]
    public async Task An_interrupted_mutation_is_never_retried_and_next_call_reconnects_to_the_same_instance()
    {
        using var environment = new TestEnvironment();
        using var host = environment.Start(106);
        var connection = new BridgeConnection(environment.Registry, host.Catalog);
        await connection.BindAsync(106, host.Entry.InstanceId, default);
        host.Tools.AfterMutation = host.Stop;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await connection.ForwardAsync(new CallToolRequestParams { Name = "mutate" }, timeout.Token);
        Assert.True(result.IsError);
        Assert.True(result.StructuredContent!.Value.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Equal(1, host.Tools.Mutations);
        host.Tools.AfterMutation = null;
        host.Restart(host.Entry.InstanceId);
        var identity = await Call(connection, "get_vs_info");
        Assert.NotEqual(true, identity.IsError);
        Assert.Equal(106, identity.StructuredContent!.Value.GetProperty("pid").GetInt32());
        Assert.Equal(1, host.Tools.Mutations);
    }

    [Fact]
    public async Task Rebinding_is_refused_while_a_tool_call_is_in_flight()
    {
        using var environment = new TestEnvironment();
        using var host = environment.Start(107);
        var connection = new BridgeConnection(environment.Registry, host.Catalog);
        await connection.BindAsync(107, host.Entry.InstanceId, default);
        var call = Call(connection, "wait");
        await host.Tools.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<McpException>(() => connection.BindAsync(107, host.Entry.InstanceId, default));
            await Task.Delay(TimeSpan.FromSeconds(11)); // Builds can outlive the connection/identity probe budget.
            Assert.False(call.IsCompleted);
        }
        finally { host.Tools.WaitFinished.TrySetResult(); }
        Assert.NotEqual(true, (await call).IsError);
    }

    [Fact]
    public async Task Stale_schema_and_wrong_endpoint_identity_are_rejected_at_binding()
    {
        using var environment = new TestEnvironment();
        using var host = environment.Start(108);
        var mismatch = new BridgeConnection(environment.Registry, ToolCatalog.Load());
        var error = await Assert.ThrowsAsync<McpException>(() => mismatch.BindAsync(108, host.Entry.InstanceId, default));
        Assert.Contains("TOOL_CATALOG_MISMATCH", error.Message);
        host.Tools.Pid = 109;
        var wrongIdentity = new BridgeConnection(environment.Registry, host.Catalog);
        error = await Assert.ThrowsAsync<McpException>(() => wrongIdentity.BindAsync(108, host.Entry.InstanceId, default));
        Assert.Contains("VS_IDENTITY_MISMATCH", error.Message);
    }

    [Fact]
    public void Registry_cleans_dead_processes_and_disposal_preserves_new_registrations()
    {
        using var environment = new TestEnvironment();
        var old = new InstanceRegistration { Pid = 110, Port = 18770, ProcessStartTimeUtcTicks = 1, InstanceId = Guid.NewGuid().ToString("N") };
        var current = new InstanceRegistration { Pid = 110, Port = 18771, ProcessStartTimeUtcTicks = 2, InstanceId = Guid.NewGuid().ToString("N") };
        var oldLease = environment.Registry.Publish(old);
        using var newLease = environment.Registry.Publish(current);
        oldLease.Dispose();
        Assert.True(environment.Registry.IsCurrent(current));
        var staleRegistry = new InstanceRegistry(environment.Directory, entry => false);
        Assert.Empty(staleRegistry.ReadAll());
        Assert.Empty(System.IO.Directory.EnumerateFiles(environment.Directory, "*.json"));
    }

    [Fact]
    public void Registry_checks_actual_process_start_time_before_exposing_a_PID()
    {
        using var environment = new TestEnvironment();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var start = process.StartTime.ToUniversalTime().Ticks;
        using var real = environment.Registry.Publish(new InstanceRegistration
        {
            Pid = process.Id, ProcessStartTimeUtcTicks = start, Port = 18770, InstanceId = Guid.NewGuid().ToString("N"),
        });
        using var reused = environment.Registry.Publish(new InstanceRegistration
        {
            Pid = process.Id, ProcessStartTimeUtcTicks = start - 1, Port = 18771, InstanceId = Guid.NewGuid().ToString("N"),
        });
        var checkedRegistry = new InstanceRegistry(environment.Directory);
        Assert.Equal(start, Assert.Single(checkedRegistry.ReadAll()).ProcessStartTimeUtcTicks);
    }

    [Fact]
    public void Bridge_deployment_leaves_a_running_installation_intact()
    {
        using var environment = new TestEnvironment();
        var source = Path.Combine(environment.Directory, "source");
        var destination = Path.Combine(environment.Directory, "installed");
        System.IO.Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "bridge.dll"), "first");
        BridgeDeployment.Install(source, destination);
        using (File.Open(Path.Combine(destination, ".bridge.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            BridgeDeployment.Install(source, destination); // An identical installation needs no update.
            File.WriteAllText(Path.Combine(source, "bridge.dll"), "second");
            Assert.Throws<IOException>(() => BridgeDeployment.Install(source, destination));
            Assert.Equal("first", File.ReadAllText(Path.Combine(destination, "bridge.dll")));
        }
        BridgeDeployment.Install(source, destination);
        Assert.Equal("second", File.ReadAllText(Path.Combine(destination, "bridge.dll")));
    }

    private static Task<CallToolResult> Call(BridgeConnection connection, string name) => connection.ForwardAsync(new CallToolRequestParams { Name = name }, default).AsTask();
    private static string Error(CallToolResult result) => result.StructuredContent!.Value.GetProperty("error").GetString()!;

    private sealed class TestEnvironment : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "vs-mcp-bridge-tests-" + Guid.NewGuid().ToString("N"));
        public InstanceRegistry Registry { get; }
        public TestEnvironment() { System.IO.Directory.CreateDirectory(Directory); Registry = new InstanceRegistry(Directory, _ => true); }
        public FakeHost Start(int pid) => new(Registry, pid);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class FakeHost : IDisposable
    {
        private McpHttpServer? server;
        private IDisposable? registration;
        public FakeTools Tools { get; }
        public InstanceRegistration Entry { get; }
        public ToolCatalog Catalog { get; }

        public FakeHost(InstanceRegistry registry, int pid)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            Entry = new InstanceRegistration { Pid = pid, Port = port, ProcessStartTimeUtcTicks = 1, InstanceId = Guid.NewGuid().ToString("N") };
            Tools = new FakeTools(pid);
            Catalog = new ToolCatalog(JsonSerializer.SerializeToNode(new { tools = Tools.Tools.OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal).Select(tool =>
            {
                var t = tool.ProtocolTool;
                return new { name = t.Name, title = t.Title, description = t.Description, readOnly = t.Annotations?.ReadOnlyHint,
                    destructive = t.Annotations?.DestructiveHint, idempotent = t.Annotations?.IdempotentHint, openWorld = t.Annotations?.OpenWorldHint,
                    inputSchema = t.InputSchema, outputSchema = t.OutputSchema };
            }).ToArray() })!.AsObject());
            Restart(Entry.InstanceId);
            registration = registry.Publish(Entry);
        }

        public void RemoveRegistration() { registration?.Dispose(); registration = null; }
        public void Stop() { server?.Dispose(); server = null; }
        public void Restart(string instanceId)
        {
            Stop();
            server = new McpHttpServer(Entry.Port, Tools, _ => { }, instanceId);
            server.Start();
        }
        public void Dispose() { Stop(); RemoveRegistration(); }
    }

    private sealed class FakeTools : IMcpToolProvider
    {
        public int Pid { get; set; }
        public int Mutations { get; private set; }
        public Action? AfterMutation { get; set; }
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WaitFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<McpServerTool> Tools { get; }
        public FakeTools(int pid) { Pid = pid; Tools = ToolJson.CreateTools(this); }

        [McpServerTool(Name = "get_vs_info", UseStructuredContent = true), Description("Identify the fake instance.")]
        public InstanceInfo Info() => new() { Pid = Pid, Product = "Visual Studio", DteVersion = "18.0", SolutionOpen = true, SolutionPath = $"C:\\Projects\\Sample{Pid}.sln" };

        [McpServerTool(Name = "mutate", UseStructuredContent = true), Description("Record one mutation.")]
        public MutationResult Mutate() { Mutations++; AfterMutation?.Invoke(); return new MutationResult(Info(), Mutations); }

        [McpServerTool(Name = "wait", UseStructuredContent = true), Description("Hold an in-flight call.")]
        public async Task<MutationResult> Wait(CancellationToken cancellationToken)
        {
            WaitStarted.TrySetResult();
            await WaitFinished.Task.WaitAsync(cancellationToken);
            return new MutationResult(Info(), Mutations);
        }
    }

    private sealed record MutationResult(InstanceInfo Instance, int Mutations);
}
