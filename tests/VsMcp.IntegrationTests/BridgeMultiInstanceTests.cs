using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using VsMcp.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace VsMcp.IntegrationTests;

[Collection("Bridge")]
public sealed class BridgeMultiInstanceTests
{
    [Fact]
    public async Task Stdio_bridge_discovers_and_controls_two_real_VS_instances_without_port_configuration()
    {
        ExpHost.EnsurePrerequisites();
        ExpHost.EnsureExtensionInstalled();
        var firstFolder = ExpHost.CopyFixture("VsMcp.DebugTestSolution", "VsMcp.DebugTestSolution.sln");
        var secondFolder = ExpHost.CopyFixture("VsMcp.DebugTestSolution", "VsMcp.DebugTestSolution.sln");
        Process? first = null;
        Process? second = null;
        try
        {
            first = ExpHost.Launch(Path.Combine(firstFolder, "VsMcp.DebugTestSolution.sln"), automaticPort: true);
            var firstEntry = await WaitForRegistration(first);
            second = ExpHost.Launch(Path.Combine(secondFolder, "VsMcp.DebugTestSolution.sln"), automaticPort: true);
            var secondEntry = await WaitForRegistration(second);
            Assert.NotEqual(firstEntry.Port, secondEntry.Port);

            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VsMcp", "Bridge", "VsMcp.Bridge.exe");
            Assert.True(File.Exists(executable), "The VSIX did not deploy the bridge to its fixed client path.");
            var builtBridge = Path.Combine(ExpHost.ProjectRoot, "src", "VsMcp.Bridge", "bin", "Release", "net10.0", "VsMcp.Bridge.dll");
            var installedBridge = Path.Combine(Path.GetDirectoryName(executable)!, "VsMcp.Bridge.dll");
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(builtBridge)),
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(installedBridge)));
            await using var bridge = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions { Command = executable }));
            Assert.Equal(51, (await bridge.ListToolsAsync()).Count);
            var discovered = await bridge.Call<JsonElement>("list_vs_instances");
            var pids = discovered.GetProperty("instances").EnumerateArray().Select(instance => instance.GetProperty("pid").GetInt32()).ToArray();
            Assert.Contains(first.Id, pids);
            Assert.Contains(second.Id, pids);

            await Bind(bridge, firstEntry);
            var identity = await bridge.Call<InstanceInfo>("get_vs_info");
            Assert.Equal(first.Id, identity.Pid);
            Assert.Equal(Path.Combine(firstFolder, "VsMcp.DebugTestSolution.sln"), identity.SolutionPath);

            await Bind(bridge, secondEntry);
            identity = await bridge.Call<InstanceInfo>("get_vs_info");
            Assert.Equal(second.Id, identity.Pid);
            await bridge.Call<JsonElement>("solution_close");
            Assert.False((await bridge.Call<InstanceInfo>("get_vs_info")).SolutionOpen);
            await Bind(bridge, firstEntry);
            Assert.True((await bridge.Call<InstanceInfo>("get_vs_info")).SolutionOpen);

            await Bind(bridge, secondEntry);
            Stop(second);
            var unavailable = await bridge.CallToolAsync("get_vs_info");
            Assert.True(unavailable.IsError);
            Assert.Equal("VS_INSTANCE_UNAVAILABLE", unavailable.StructuredContent!.Value.GetProperty("error").GetString());
            Assert.Equal(51, (await bridge.ListToolsAsync()).Count); // The external MCP survives a closed VS.
            await Bind(bridge, firstEntry);
            Assert.Equal(first.Id, (await bridge.Call<InstanceInfo>("get_vs_info")).Pid);
        }
        finally
        {
            Stop(second);
            Stop(first);
            foreach (var folder in new[] { firstFolder, secondFolder })
            {
                var root = Path.GetDirectoryName(folder)!;
                if (!root.StartsWith(Path.Combine(Path.GetTempPath(), "vs-mcp-tests-"), StringComparison.OrdinalIgnoreCase))
                    throw new XunitException("Unexpected fixture cleanup path: " + root);
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task Bind(McpClient bridge, InstanceRegistration entry)
    {
        var result = await bridge.Call<JsonElement>("connect_vs_instance", new { pid = entry.Pid, instanceId = entry.InstanceId });
        Assert.Equal(entry.Pid, result.GetProperty("instance").GetProperty("pid").GetInt32());
    }

    private static async Task<InstanceRegistration> WaitForRegistration(Process process)
    {
        var registry = new InstanceRegistry();
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(120))
        {
            var entry = registry.ReadAll().SingleOrDefault(entry => entry.Pid == process.Id);
            if (entry != null)
            {
                await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://127.0.0.1:{entry.Port}/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { ["X-VsMcp-Instance"] = entry.InstanceId },
                }));
                await ExpHost.VerifyRunningPackage(client);
                return entry;
            }
            if (process.HasExited) throw new XunitException("The owned experimental instance exited before registering.");
            var failure = ExpHost.FindLoadFailure(started);
            if (failure != null) throw new XunitException("The extension failed to load: " + failure);
            await Task.Delay(1000);
        }
        throw new XunitException("The owned VS instance did not publish its MCP registration.");
    }

    private static void Stop(Process? process)
    {
        if (process == null || process.HasExited) return;
        process.Kill(entireProcessTree: true);
        process.WaitForExit(10000);
    }
}
