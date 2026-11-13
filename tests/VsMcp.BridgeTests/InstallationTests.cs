#nullable enable
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Client;
using Xunit;

namespace VsMcp.BridgeTests;

public sealed class InstallationTests
{
    [Fact]
    public async Task Setup_installs_without_VS_or_external_runtime_and_reinstalls_while_in_use()
    {
        using var install = new Installation();
        await install.Setup();
        var expectedVersion = typeof(VsMcp.Bridge.Program).Assembly.GetName().Version!.ToString(3);
        var selected = File.ReadAllText(install.Pointer);
        Assert.StartsWith(expectedVersion + "-", selected);
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(install.Root, "mcp-config.json")));
        Assert.Equal(install.Launcher, config.RootElement.GetProperty("mcpServers").GetProperty("visual-studio").GetProperty("command").GetString());
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = install.Launcher,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["DOTNET_ROOT"] = Path.Combine(install.Root, "no-dotnet"),
                ["DOTNET_ROOT_X64"] = Path.Combine(install.Root, "no-dotnet"),
                ["DOTNET_HOST_TRACE"] = "1",
            },
            StandardErrorLines = trace.Enqueue,
        }));
        Assert.Equal(expectedVersion, client.ServerInfo?.Version);
        Assert.Equal(87, (await client.ListToolsAsync()).Count);
        Assert.Contains(trace, line => line.Contains(Path.Combine(install.Root, "versions", selected.Trim(), "coreclr.dll"), StringComparison.OrdinalIgnoreCase));
        await install.Setup();
        Assert.Equal(selected, File.ReadAllText(install.Pointer));
        Assert.Equal(87, (await client.ListToolsAsync()).Count);
        // A separately compiled fixture selects a different immutable directory with the same valid payload.
        var original = Path.Combine(install.Root, "versions", selected.Trim(), "VsMcp.Bridge.exe");
        var originalHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original));
        await install.Setup(upgrade: true);
        Assert.NotEqual(selected, File.ReadAllText(install.Pointer));
        Assert.Equal(originalHash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original)));
        Assert.Equal(87, (await client.ListToolsAsync()).Count); // Existing connection is still usable.
        await using var next = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions { Command = install.Launcher }));
        Assert.Equal(87, (await next.ListToolsAsync()).Count);
    }

    [Fact]
    public async Task Terminating_launcher_closes_only_its_Bridge_child()
    {
        using var install = new Installation();
        await install.Setup();
        using var launcher = Process.Start(new ProcessStartInfo(install.Launcher)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        })!;
        // Identify our child by its unique installation path without interacting with other clients.
        Process? child = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (child == null)
            {
                foreach (var candidate in Process.GetProcessesByName("VsMcp.Bridge"))
                {
                    try
                    {
                        if (candidate.MainModule?.FileName?.StartsWith(install.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true)
                        { child = candidate; break; }
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    if (candidate != child) candidate.Dispose();
                }
                if (child == null) await Task.Delay(50, timeout.Token);
            }
            launcher.Kill();
            await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (!launcher.HasExited) { launcher.Kill(); await launcher.WaitForExitAsync(); }
            child?.Dispose();
        }
    }

    [Fact]
    public async Task Incomplete_or_corrupt_payload_never_replaces_previous_selection()
    {
        using var install = new Installation();
        await install.Setup();
        var version = File.ReadAllText(install.Pointer).Trim();
        File.WriteAllText(Path.Combine(install.Root, "versions", version, "VsMcp.Bridge.dll"), "corrupt");
        // A previous selection must survive even if the staged new version fails verification.
        File.WriteAllText(install.Pointer, "previous-version\r\n");
        await install.Setup(expectSuccess: false);
        Assert.Equal("previous-version\r\n", File.ReadAllText(install.Pointer));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("missing-version")]
    [InlineData("")]
    public async Task Launcher_rejects_invalid_or_missing_payload_without_stdout(string selection)
    {
        using var install = new Installation();
        await install.Setup();
        File.WriteAllText(install.Pointer, selection);
        var start = new ProcessStartInfo(install.Launcher) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(0, process.ExitCode);
        Assert.Empty(await stdout);
        Assert.Contains("Run Bridge Setup", await stderr);
    }

    [Fact]
    public async Task Uninstall_removes_launcher_selection_and_version_payload()
    {
        using var install = new Installation();
        await install.Setup();
        var version = File.ReadAllText(install.Pointer).Trim();
        await install.Setup(upgrade: true);
        var upgradedVersion = File.ReadAllText(install.Pointer).Trim();
        var start = new ProcessStartInfo(Path.Combine(install.Root, "unins000.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(0, process.ExitCode);
        Assert.False(File.Exists(install.Launcher));
        Assert.False(File.Exists(install.Pointer));
        Assert.False(File.Exists(Path.Combine(install.Root, "versions", version, "VsMcp.Bridge.exe")));
        Assert.False(File.Exists(Path.Combine(install.Root, "versions", upgradedVersion, "VsMcp.Bridge.exe")));
    }

    private sealed class Installation : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vs-mcp-install-tests-" + Guid.NewGuid().ToString("N"), "folder with spaces-測試");
        public string Launcher => Path.Combine(Root, "VsMcp.Launcher.exe");
        public string Pointer => Path.Combine(Root, "current.txt");

        public async Task Setup(bool expectSuccess = true, bool upgrade = false)
        {
            var setup = Path.GetFullPath(typeof(InstallationTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == (upgrade ? "BridgeUpgradeSetup" : "BridgeSetup")).Value!);
            Assert.True(File.Exists(setup), "Build Bridge Setup first: eng/Build-BridgeSetup.ps1 -IncludeUpgradeTestFixture");
            Directory.CreateDirectory(Root);
            var start = new ProcessStartInfo(setup) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/SP-", "/NORESTART", "/TestInstall=1", "/DIR=" + Root, "/LOG=" + Path.Combine(Root, "setup.log") }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
            catch { process.Kill(entireProcessTree: true); throw; }
            var log = File.Exists(Path.Combine(Root, "setup.log")) ? string.Join('\n', File.ReadLines(Path.Combine(Root, "setup.log")).TakeLast(35)) : "No installer log";
            Assert.True(expectSuccess ? process.ExitCode == 0 : process.ExitCode != 0, $"Setup exited {process.ExitCode}: {log}");
        }

        public void Dispose()
        {
            // Only this test's random temporary root; never invoke a user's installed uninstaller.
            var owned = Path.GetFullPath(Path.GetDirectoryName(Root)!);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(owned), temp, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(owned).StartsWith("vs-mcp-install-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned temporary installation.");
            if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
        }
    }
}
