using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using VsMcp.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace VsMcp.IntegrationTests;

[Collection("Bridge")]
public sealed class BridgeMultiInstanceTests
{
    [Fact]
    public async Task Installed_bridge_launches_and_verifies_an_independent_VS_instance()
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

            // Setup smoke tests install into an isolated directory; never update a user's Bridge here.
            var bridgeRoot = Environment.GetEnvironmentVariable("VS_MCP_TEST_BRIDGE_ROOT")
                ?? throw new XunitException("Set VS_MCP_TEST_BRIDGE_ROOT to an isolated Bridge Setup installation (eng/Test-Integration.ps1).");
            var executable = Path.Combine(bridgeRoot, "VsMcp.Launcher.exe");
            Assert.True(File.Exists(executable), "The isolated Bridge Setup installation has no launcher.");
            var selected = File.ReadAllText(Path.Combine(bridgeRoot, "current.txt")).Trim();
            Assert.Matches("^[0-9][a-z0-9.-]*$", selected);
            Assert.DoesNotContain("..", selected);
            var builtBridge = Path.Combine(ExpHost.ProjectRoot, "src", "VsMcp.Bridge", "bin", "Release", "net10.0", "win-x64", "VsMcp.Bridge.dll");
            var installedBridge = Path.Combine(bridgeRoot, "versions", selected, "VsMcp.Bridge.dll");
            foreach (var runtimeFile in new[] { "hostfxr.dll", "hostpolicy.dll", "coreclr.dll" })
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(installedBridge)!, runtimeFile)), "Missing bundled runtime: " + runtimeFile);
            using var runtimeConfig = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(installedBridge, ".runtimeconfig.json")));
            Assert.False(runtimeConfig.RootElement.GetProperty("runtimeOptions").TryGetProperty("framework", out _));
            Assert.True(runtimeConfig.RootElement.GetProperty("runtimeOptions").TryGetProperty("includedFrameworks", out _));
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(builtBridge)),
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(installedBridge)));
            using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            using var errors = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            using var launcher = ExpHost.StartBridgeOnDesktop(executable, input.ClientSafePipeHandle.DangerousGetHandle(),
                output.ClientSafePipeHandle.DangerousGetHandle(), errors.ClientSafePipeHandle.DangerousGetHandle());
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            errors.DisposeLocalCopyOfClientHandle();
            using var errorReader = new StreamReader(errors);
            var diagnostics = errorReader.ReadToEndAsync();
            await using var bridge = await McpClient.CreateAsync(new StreamClientTransport(input, output));
            Assert.Equal(88, (await bridge.ListToolsAsync()).Count);
            await Bind(bridge, firstEntry);
            var solutionPath = Path.Combine(secondFolder, "Debug Test Solution.sln");
            File.Move(Path.Combine(secondFolder, "VsMcp.DebugTestSolution.sln"), solutionPath);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var launch = await bridge.CallToolAsync("launch_vs_instance", new Dictionary<string, object?>
            {
                ["solutionPath"] = solutionPath,
                ["devenvPath"] = first.MainModule!.FileName,
                ["rootSuffix"] = ExpHost.RootSuffix,
            }, cancellationToken: timeout.Token);
            var details = launch.StructuredContent;
            if (details is { } content && content.TryGetProperty("pid", out var id) && id.GetInt32() != first.Id
                && !(launch.IsError == true && content.GetProperty("error").GetString() == "VS_LAUNCH_EXITED"))
                second = Process.GetProcessById(id.GetInt32());
            Assert.False(launch.IsError == true, ToolCalls.TextOf(launch));
            Assert.NotNull(second);
            var secondEntry = await WaitForRegistration(second);
            Assert.Equal(secondEntry.InstanceId, details!.Value.GetProperty("instanceId").GetString());
            Assert.Equal(solutionPath, details.Value.GetProperty("instance").GetProperty("solutionPath").GetString());
            Assert.False(details.Value.GetProperty("reused").GetBoolean());
            Assert.NotEqual(firstEntry.Port, secondEntry.Port);

            var reused = await bridge.Call<JsonElement>("launch_vs_instance", new { solutionPath });
            Assert.True(reused.GetProperty("reused").GetBoolean());
            Assert.Equal(second.Id, reused.GetProperty("pid").GetInt32());
            Assert.Equal(secondEntry.InstanceId, reused.GetProperty("instanceId").GetString());
            Assert.Equal(first.Id, (await bridge.Call<InstanceInfo>("get_vs_info")).Pid);

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

            Stop(first);
            var unavailable = await bridge.CallToolAsync("get_vs_info");
            Assert.True(unavailable.IsError);
            Assert.Equal("VS_INSTANCE_UNAVAILABLE", unavailable.StructuredContent!.Value.GetProperty("error").GetString());
            Assert.Equal(88, (await bridge.ListToolsAsync()).Count);
            await Bind(bridge, secondEntry);
            Assert.Equal(second.Id, (await bridge.Call<InstanceInfo>("get_vs_info")).Pid);

            // Closing the launcher's cleanup job must leave the IDE and its endpoint alive.
            launcher.Kill();
            await launcher.WaitForExitAsync(timeout.Token);
            await diagnostics.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(second.HasExited);
            await WaitForRegistration(second);
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
