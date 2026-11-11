using System.Diagnostics;
using System.Net.Sockets;
using ModelContextProtocol.Client;
using Xunit;
using Xunit.Sdk;

namespace VsMcp.IntegrationTests;

/// <summary>
/// Prepares and runs the experimental Visual Studio instance the tests talk to: installs the Release package once per
/// test run, copies a fixture solution to a temporary folder, starts the instance on its own port with that solution and
/// closes exactly that instance afterwards. An instance of the normal profile is never touched.
/// </summary>
internal static class ExpHost
{
    private const string ExtensionId = "VsMcp.ea19e206-02c0-4909-a752-2857e69a29a6";

    private static readonly Lazy<bool> InstallResult = new(() => { InstallExtension(); return true; });
    private static readonly Lazy<bool> PreflightResult = new(Preflight);
    private static readonly Lazy<TestDesktop> Desktop = new(() =>
    {
        var desktop = new TestDesktop();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => desktop.Dispose();
        Log("isolated desktop " + desktop.Name);
        return desktop;
    });

    /// <summary>Progress goes to stderr and to this file, so a long step is visible and a failure says where it stopped.</summary>
    public static string LogFile { get; } = Path.Combine(Path.GetTempPath(), "vs-mcp-tests.log");

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [exp] {message}";
        Console.Error.WriteLine(line);
        try { File.AppendAllText(LogFile, line + Environment.NewLine); }
        catch (IOException) { /* the log is a convenience */ }
    }

    public static int Port { get; } = int.TryParse(Environment.GetEnvironmentVariable("VS_MCP_TEST_PORT"), out var port) ? port : 18772;

    // A separate experimental hive allows validation without altering an existing developer hive.
    public static string RootSuffix { get; } = ReadRootSuffix();
    private static string ReadRootSuffix()
    {
        var value = Environment.GetEnvironmentVariable("VS_MCP_TEST_ROOT_SUFFIX") ?? "Exp";
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9]{0,31}$"))
            throw new XunitException("VS_MCP_TEST_ROOT_SUFFIX must be a non-empty alphanumeric experimental suffix.");
        return value;
    }

    public static string Endpoint => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>The repository folder that holds <c>tests/VsMcp.TestProjects</c> and <c>src</c>.</summary>
    public static string ProjectRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "tests", "VsMcp.TestProjects"))) return directory.FullName;
            }

            throw new XunitException("Cannot find the vs-mcp folder above " + AppContext.BaseDirectory);
        }
    }

    private static string VsRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("VS_MCP_VS_ROOT");
            if (!string.IsNullOrEmpty(configured)) return configured;

            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            var output = Run(vswhere, "-latest -products * -property installationPath", 30, ignoreExitCode: true).Trim();
            if (output.Length == 0) throw new XunitException("Cannot find Visual Studio. Set VS_MCP_VS_ROOT to its installation folder.");
            return output.Split('\n')[0].Trim();
        }
    }

    private static string Ide(string file) => Path.Combine(VsRoot, "Common7", "IDE", file);

    /// <summary>
    /// Runs installer/configuration helpers on the isolated desktop and waits without capturing output.
    /// </summary>
    private static void RunHidden(string file, string arguments, int timeoutSeconds, bool ignoreExitCode = false)
    {
        var info = new ProcessStartInfo(file, arguments) { UseShellExecute = false };
        using var process = Desktop.Value.Start(info);
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill(entireProcessTree: true);
            throw new XunitException($"{Path.GetFileName(file)} {arguments} did not finish in {timeoutSeconds} s.");
        }

        if (!ignoreExitCode && process.ExitCode != 0) throw new XunitException($"{Path.GetFileName(file)} {arguments} exited with {process.ExitCode}.");
    }

    /// <summary>Runs a program to completion and returns its output.</summary>
    private static string Run(string file, string arguments, int timeoutSeconds, bool ignoreExitCode = false, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (workingDirectory != null) info.WorkingDirectory = workingDirectory;
        using var process = Process.Start(info) ?? throw new XunitException("Cannot start " + file);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill(entireProcessTree: true);
            throw new XunitException($"{Path.GetFileName(file)} {arguments} did not finish in {timeoutSeconds} s.");
        }

        // Background servers that the program leaves behind can hold the pipes open, so never wait for them indefinitely.
        var text = output.Wait(TimeSpan.FromSeconds(5)) ? output.Result : "";
        if (!ignoreExitCode && process.ExitCode != 0)
            throw new XunitException($"{Path.GetFileName(file)} {arguments} exited with {process.ExitCode}: {text}{(error.Wait(TimeSpan.FromSeconds(1)) ? error.Result : "")}");
        return text;
    }

    /// <summary>
    /// Checks everything that can already be known to be wrong, before any slow step runs, and fails at once:
    /// the package, a running experimental instance and the port.
    /// </summary>
    public static void EnsurePrerequisites() => _ = PreflightResult.Value;

    private static bool Preflight()
    {
        File.WriteAllText(LogFile, "");
        Log("checking prerequisites");
        _ = VsRoot;
        if (Process.GetProcessesByName("devenv").Any(p => p.MainWindowTitle.Contains("Experimental", StringComparison.OrdinalIgnoreCase)))
            throw new XunitException("An experimental Visual Studio instance is already open. Close it first; the tests start their own.");
        if (PortInUse())
            throw new XunitException($"Port {Port} is already in use, so the tests cannot start their own instance. Close what uses it or set VS_MCP_TEST_PORT.");
        if (!File.Exists(Package))
            throw new XunitException($"Build the Release package first: {Package} does not exist.");
        Log($"package {Identity}");
        return true;
    }

    /// <summary>What identifies the package under test: its version and the SHA-256 of the file and of the VsMcp.dll inside it.</summary>
    public sealed record PackageIdentity(string Version, string PackageSha256, string DllSha256)
    {
        public override string ToString() => $"{Version} (vsix sha256 {PackageSha256[..12]}, VsMcp.dll sha256 {DllSha256[..12]})";
    }

    public static PackageIdentity Identity => _identity ??= ReadIdentity();

    private static PackageIdentity? _identity;

    private static PackageIdentity ReadIdentity()
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(Package);
        var manifest = archive.GetEntry("extension.vsixmanifest") ?? throw new XunitException("The package has no extension.vsixmanifest.");
        using var manifestStream = manifest.Open();
        var version = System.Xml.Linq.XDocument.Load(manifestStream).Descendants().First(element => element.Name.LocalName == "Identity").Attribute("Version")!.Value;

        var dll = archive.GetEntry("VsMcp.dll") ?? throw new XunitException("The package has no VsMcp.dll.");
        using var dllStream = dll.Open();
        return new PackageIdentity(version, Sha256(File.ReadAllBytes(Package)), Sha256(dllStream));
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Sha256(Stream stream) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();

    /// <summary>
    /// Proves the instance runs the package under test: the VsMcp.dll that Visual Studio loaded must have the version
    /// and SHA-256 of the one inside the package. Anything else (an old install, a stale configuration) fails at once.
    /// </summary>
    public static async Task VerifyRunningPackage(McpClient client)
    {
        const string code = "var a = System.AppDomain.CurrentDomain.GetAssemblies().First(x => x.GetName().Name == \"VsMcp\");" +
            "byte[] hash; using (var sha = System.Security.Cryptography.SHA256.Create()) hash = sha.ComputeHash(System.IO.File.ReadAllBytes(a.Location));" +
            "return new { version = a.GetName().Version.ToString(), sha256 = System.BitConverter.ToString(hash).Replace(\"-\", \"\").ToLowerInvariant(), location = a.Location, windowHandle = DTE.MainWindow.HWnd.ToInt64() };";
        var running = (await client.Call<CodeResult>("execute_code", new { code })).Result;

        var expected = Identity;
        var versionMatches = Version.TryParse(expected.Version, out var wanted) && Version.TryParse(running.Version, out var loaded)
            && wanted.Major == loaded.Major && wanted.Minor == loaded.Minor && Math.Max(wanted.Build, 0) == Math.Max(loaded.Build, 0);
        if (!versionMatches || !string.Equals(running.Sha256, expected.DllSha256, StringComparison.OrdinalIgnoreCase))
            throw new XunitException($"The instance does not run the package under test. Package: version {expected.Version}, VsMcp.dll sha256 {expected.DllSha256}. " +
                                     $"Running: version {running.Version}, sha256 {running.Sha256} ({running.Location}). Rebuild the package or reinstall it.");
        Log($"verified: the instance runs VsMcp.dll {running.Version} sha256 {running.Sha256[..12]}");
        try { Desktop.Value.VerifyWindow(new IntPtr(running.WindowHandle)); }
        catch (Exception error) { throw new XunitException("Background window isolation failed: " + error.Message); }
        var hidden = await client.Call<System.Text.Json.JsonElement>("show_vs_window", new { visible = false });
        Assert.False(hidden.GetProperty("visible").GetBoolean());
        Log("verified: isolated desktop and hidden main window");
    }

    private sealed record LoadedAssembly(string Version, string Sha256, string Location, long WindowHandle);

    private sealed record CodeResult(LoadedAssembly Result);

    private static string Package => Path.Combine(ProjectRoot, "src", "VsMcp.Vsix", "bin", "Release", "net472", "VsMcp.vsix");

    /// <summary>Installs the Release package into the experimental hive, once per test run, unless VS_MCP_SKIP_INSTALL is set.</summary>
    public static void EnsureExtensionInstalled() => _ = InstallResult.Value;

    private static void InstallExtension()
    {
        if (Environment.GetEnvironmentVariable("VS_MCP_SKIP_INSTALL") == "1") return;

        if (IsInstalled())
        {
            Log("the experimental hive already has this package (matching DLL, redirects and bridge files); not installing it again");
            return;
        }

        InstallPackage();
    }

    private static void InstallPackage()
    {
        Log("installing the extension into the experimental hive");
        RunHidden(Ide("VSIXInstaller.exe"), $"/quiet /rootSuffix:{RootSuffix} /uninstall:{ExtensionId}", 120, ignoreExitCode: true);
        RunHidden(Ide("VSIXInstaller.exe"), $"/quiet /rootSuffix:{RootSuffix} \"{Package}\"", 120);

        // Without this a new or changed .pkgdef is not merged and the extension fails to load. It must finish first.
        Log("updating the experimental configuration");
        RunHidden(Ide("devenv.exe"), $"/rootsuffix {RootSuffix} /updateconfiguration", 180);
    }

    private static bool s_reinstalled;

    /// <summary>
    /// The hive can hold the right VsMcp.dll while its registration is stale (an update that was interrupted, or a
    /// Visual Studio F5 deployment), which the file check cannot see. When an instance then fails to load the extension,
    /// install the package again, once per test run. Returns false when that was already done or installing is disabled.
    /// </summary>
    public static bool ReinstallOnce()
    {
        if (s_reinstalled || Environment.GetEnvironmentVariable("VS_MCP_SKIP_INSTALL") == "1") return false;
        s_reinstalled = true;
        Log("the extension failed to load although the hive has the package; installing it again");
        InstallPackage();
        return true;
    }

    /// <summary>
    /// True when an extension folder holds the package's DLL, redirects and bridge files, so the install
    /// (which opens an installer window and takes half a minute) can be skipped. The running instance is verified later anyway.
    /// </summary>
    private static bool IsInstalled()
    {
        var hives = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "VisualStudio");
        if (!Directory.Exists(hives)) return false;
        return Directory.EnumerateDirectories(hives).Where(directory => Path.GetFileName(directory).EndsWith(RootSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(hive => Path.Combine(hive, "Extensions"))
            .Where(Directory.Exists)
            .SelectMany(extensions => Directory.EnumerateFiles(extensions, "VsMcp.dll", SearchOption.AllDirectories))
            .Any(dll =>
            {
                try
                {
                    if (Sha256(File.ReadAllBytes(dll)) != Identity.DllSha256) return false;
                    using var archive = System.IO.Compression.ZipFile.OpenRead(Package);
                    return archive.Entries.Where(entry => entry.Name.Length != 0 &&
                        (entry.FullName == "VsMcp.Redirects.pkgdef" || entry.FullName.StartsWith("Bridge/", StringComparison.Ordinal)))
                        .All(entry =>
                        {
                            var installed = Path.Combine(Path.GetDirectoryName(dll)!, entry.FullName);
                            if (!File.Exists(installed)) return false;
                            using var stream = entry.Open();
                            return Sha256(File.ReadAllBytes(installed)) == Sha256(stream);
                        });
                }
                catch (IOException) { return false; }
            });
    }

    /// <summary>Copies a fixture to a new temporary folder (without build output) and restores it, so builds never write into the repository.</summary>
    public static string CopyFixture(string name, string solutionFile)
    {
        var source = Path.Combine(ProjectRoot, "tests", "VsMcp.TestProjects", name);
        var target = Path.Combine(Path.GetTempPath(), "vs-mcp-tests-" + Guid.NewGuid().ToString("N"), name);
        Copy(source, target);
        Log($"restoring {name}");
        if (Directory.EnumerateFiles(target, "*.csproj", SearchOption.AllDirectories).Any())
            Run("dotnet", $"restore \"{Path.Combine(target, solutionFile)}\" --disable-build-servers", 120, workingDirectory: target);
        var testCases = Path.Combine(target, "TestCases", "TestCases.csproj");
        if (File.Exists(testCases)) Run("dotnet", $"build \"{testCases}\" --disable-build-servers -v:q", 120, workingDirectory: target);
        return target;
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name is "bin" or "obj" or ".vs") continue;
            Copy(directory, Path.Combine(target, name));
        }
    }

    /// <summary>
    /// The activity log of the experimental instance (written because it is started with /log) names an extension that
    /// failed to load. Reading it lets the tests stop with that message instead of waiting for an endpoint that never appears.
    /// </summary>
    public static string? FindLoadFailure(DateTime startedUtc)
    {
        var hives = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "VisualStudio");
        if (!Directory.Exists(hives)) return null;

        foreach (var log in Directory.EnumerateDirectories(hives).Where(directory => Path.GetFileName(directory).EndsWith(RootSuffix, StringComparison.OrdinalIgnoreCase)).Select(directory => Path.Combine(directory, "ActivityLog.xml")).Where(File.Exists))
        {
            if (File.GetLastWriteTimeUtc(log) < startedUtc.AddSeconds(-5)) continue;
            try
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var match = System.Text.RegularExpressions.Regex.Match(reader.ReadToEnd(), @"CreateInstance failed for package \[VsMcpPackage\][^<]*");
                if (match.Success) return System.Net.WebUtility.HtmlDecode(match.Value).Split('\n')[0].Trim();
            }
            catch (IOException)
            {
                // The instance is still writing it; the next poll reads it again.
            }
        }

        return null;
    }

    public static bool PortInUse()
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("127.0.0.1", Port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    internal static void VerifyBackgroundProcesses(System.Text.Json.JsonElement status)
    {
        if (status.TryGetProperty("processes", out var processes))
            foreach (var process in processes.EnumerateArray())
            {
                // A dump PID identifies a recorded process, not a live OS target on a desktop.
                if (process.TryGetProperty("isDump", out var isDump) && isDump.GetBoolean()) continue;
                Desktop.Value.VerifyProcess(process.GetProperty("pid").GetInt32());
            }
        if (status.TryGetProperty("withoutDebuggingRequest", out var launch) && launch.ValueKind == System.Text.Json.JsonValueKind.Object)
            VerifyBackgroundProcesses(launch);
    }

    internal static bool WindowIsVisible(IntPtr window) => Desktop.Value.WindowIsVisible(window);

    /// <summary>Starts the experimental instance with a solution; the extension serves its MCP endpoint on <see cref="Port"/>.</summary>
    public static Process Launch(string solutionPath, bool automaticPort = false)
    {
        Log($"starting the experimental instance with {solutionPath}");
        // /log must come last and without an argument: it takes the next argument as its output file.
        var info = new ProcessStartInfo(Ide("devenv.exe"), $"\"{solutionPath}\" /rootsuffix {RootSuffix} /NoSplash /log") { UseShellExecute = false };
        if (automaticPort) info.Environment.Remove("VS_MCP_PORT");
        else info.Environment["VS_MCP_PORT"] = Port.ToString();
        return Desktop.Value.Start(info);
    }
}

/// <summary>
/// One MCP connection to an experimental instance that these tests started with a copy of a fixture solution.
/// A test class gets a fresh instance per fixture group, and every test in it shares that instance.
/// </summary>
public abstract class McpSession : IAsyncLifetime
{
    private Process? _launched;
    private int _instanceId;
    private string? _workFolder;
    private McpClient? _client;

    /// <summary>The fixture folder under tests/VsMcp.TestProjects.</summary>
    protected abstract string Fixture { get; }

    /// <summary>The solution file inside it.</summary>
    protected abstract string SolutionFile { get; }

    public McpClient Client => _client ?? throw new InvalidOperationException("The instance did not start.");

    /// <summary>The folder with the copy of the fixture that the instance has open.</summary>
    public string SolutionDirectory => _workFolder ?? throw new InvalidOperationException("The instance did not start.");

    public async Task InitializeAsync()
    {
        ExpHost.EnsurePrerequisites();
        ExpHost.EnsureExtensionInstalled();
        _workFolder = ExpHost.CopyFixture(Fixture, SolutionFile);
        var solutionPath = Path.Combine(_workFolder, SolutionFile);

        for (var attempt = 0; ; attempt++)
        {
            _launched = ExpHost.Launch(solutionPath);
            string? failure;
            try { failure = await WaitForInstance(solutionPath); }
            catch { KillInstance(); throw; }
            if (failure == null) return;

            KillInstance();
            if (attempt == 0 && ExpHost.ReinstallOnce()) continue;
            throw new XunitException($"The extension failed to load in the experimental instance: {failure}. Check the installed package and that /updateconfiguration finished. Progress log: {ExpHost.LogFile}");
        }
    }

    /// <summary>
    /// Visual Studio takes a while to start: polls until the endpoint serves this exact solution and runs the package under
    /// test (returns null), or until the activity log names a load failure of the extension (returns it). Fails after 120 s.
    /// </summary>
    private async Task<string?> WaitForInstance(string solutionPath)
    {
        var started = DateTime.UtcNow;
        var deadline = started.AddSeconds(120);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(ExpHost.Endpoint), TransportMode = HttpTransportMode.StreamableHttp });
                var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
                var info = await client.Call<InstanceInfo>("get_vs_info");
                if (info.Pid != _launched?.Id) throw new XunitException("The endpoint belongs to a different VS process; the test will not control or close it.");
                _instanceId = info.Pid;
                if (string.Equals(info.SolutionPath, solutionPath, StringComparison.OrdinalIgnoreCase))
                {
                    ExpHost.Log("the instance is serving the solution");
                    await ExpHost.VerifyRunningPackage(client);
                    _client = client;
                    return null;
                }

                await client.DisposeAsync();
                last = new XunitException($"The instance has {info.SolutionPath ?? "no solution"} open, not {solutionPath}.");
            }
            catch (XunitException)
            {
                throw; // the wrong package or solution is a test failure, not something to retry
            }
            catch (Exception ex)
            {
                last = ex;
            }

            var failure = ExpHost.FindLoadFailure(started);
            if (failure != null) return failure;

            await Task.Delay(3000);
        }

        throw new XunitException($"No MCP endpoint at {ExpHost.Endpoint} serving {solutionPath} after 120 s. Last error: {last?.Message}. Progress log: {ExpHost.LogFile}");
    }

    /// <summary>Closes only the instance that this session started: the one whose endpoint answered, and the process we launched.</summary>
    private void KillInstance()
    {
        foreach (var id in new[] { _instanceId, _launched?.Id ?? 0 }.Where(id => id != 0).Distinct())
        {
            try { Process.GetProcessById(id).Kill(entireProcessTree: true); }
            catch (Exception) { /* already gone */ }
        }

        _instanceId = 0;
        _launched = null;
    }

    public async Task DisposeAsync()
    {
        if (_client != null) await _client.DisposeAsync();

        KillInstance();

        try { if (_workFolder != null) Directory.Delete(Path.GetDirectoryName(_workFolder)!, recursive: true); }
        catch (Exception) { /* a closing instance can still hold files; the temp folder is disposable */ }
    }
}

/// <summary>A copy of tests/VsMcp.TestProjects/VsMcp.BuildTestSolution/VsMcp.BuildTestSolution.sln: projects, folders and build behavior.</summary>
public sealed class FixtureSession : McpSession
{
    protected override string Fixture => "VsMcp.BuildTestSolution";

    protected override string SolutionFile => "VsMcp.BuildTestSolution.sln";
}

/// <summary>A copy of tests/VsMcp.TestProjects/VsMcp.DebugTestSolution/VsMcp.DebugTestSolution.sln: a console application that sleeps for 30 seconds.</summary>
public sealed class DebugSession : McpSession
{
    protected override string Fixture => "VsMcp.DebugTestSolution";

    protected override string SolutionFile => "VsMcp.DebugTestSolution.sln";
}

public sealed class NativeSession : McpSession
{
    protected override string Fixture => "VsMcp.CppTestSolution";
    protected override string SolutionFile => "VsMcp.CppTestSolution.sln";
}

[CollectionDefinition("Native")]
public sealed class NativeCollection : ICollectionFixture<NativeSession> { }

[CollectionDefinition("Fixture")]
public sealed class FixtureCollection : ICollectionFixture<FixtureSession>
{
}

[CollectionDefinition("Debug")]
public sealed class DebugCollection : ICollectionFixture<DebugSession>
{
}
