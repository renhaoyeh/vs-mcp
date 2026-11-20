using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace VsMcp.Bridge;

internal sealed class BridgeTools(BridgeConnection connection)
{
    [McpServerTool(Name = "launch_vs_instance", Title = "Find or launch Visual Studio for a solution", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Find the authorized solution in running Visual Studio instances, or start a new process and verify its solution. Use forceNew only when the user explicitly wants a separate process even if the solution is already open. Does not change the bridge binding. If launch is interrupted or unconfirmed, inspect the reported PID and list_vs_instances before retrying.")]
    public Task<CallToolResult> Launch(
        [Description("Absolute path to an existing .sln or .slnx file.")] string solutionPath,
        [Description("Absolute devenv.exe path from a Visual Studio 2026 installation. Required when multiple installations are found.")] string? devenvPath = null,
        [Description("Optional explicitly requested experimental hive suffix. Omit for the normal Visual Studio profile."), RegularExpression("[a-zA-Z0-9]{1,32}")] string? rootSuffix = null,
        [Description("Always open a new process, even if this solution is already open. Defaults to false.")] bool forceNew = false,
        CancellationToken cancellationToken = default) => connection.LaunchAsync(solutionPath, devenvPath, rootSuffix, forceNew, cancellationToken);

    [McpServerTool(Name = "list_vs_instances", Title = "List Visual Studio instances", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List registered running Visual Studio instances, their PID, instanceId, endpoint and live solution identity. Also reports the current binding. Works when no VS is running; never selects an instance.")]
    public Task<DiscoveryResult> List([Description("Cancellation.")] CancellationToken cancellationToken = default) => connection.DiscoverAsync(cancellationToken);

    [McpServerTool(Name = "connect_vs_instance", Title = "Connect to a Visual Studio instance", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Explicitly bind this bridge to the PID and instanceId returned by list_vs_instances. Checks endpoint identity and tool compatibility. Wait for active calls to finish before switching. A restarted VS requires a new explicit binding.")]
    public Task<BindingResult> Connect(
        [Description("Process ID returned by list_vs_instances."), Range(1, int.MaxValue)] int pid,
        [Description("Exact instanceId returned by list_vs_instances; prevents binding to a reused PID or changed registration.")] string instanceId,
        CancellationToken cancellationToken = default) => connection.BindAsync(pid, instanceId, cancellationToken);
}

// The IDE tools retain the SDK schemas generated from their attributed production methods.
// This adapter only forwards SDK request/result objects; it does not implement JSON-RPC.
internal sealed class ForwardingTool(Tool tool, BridgeConnection connection) : McpServerTool
{
    public override Tool ProtocolTool => tool;
    public override IReadOnlyList<object> Metadata => [];
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
        connection.ForwardAsync(request.Params!, cancellationToken);
}
