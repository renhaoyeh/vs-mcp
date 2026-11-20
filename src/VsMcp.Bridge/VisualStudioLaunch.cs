using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol;

namespace VsMcp.Bridge;

internal static class VisualStudioLaunch
{
    public static string ValidateSolutionPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('"')
            || !new[] { ".sln", ".slnx" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            || !File.Exists(path))
            throw new McpException("INVALID_SOLUTION_PATH: Provide the absolute path to an existing .sln or .slnx file.");
        return Path.GetFullPath(path);
    }

    public static async Task<string> FindDevenvAsync(string? requestedPath, CancellationToken cancellationToken)
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
            throw new McpException("VS_INSTALLATION_DISCOVERY_FAILED: Visual Studio Installer's vswhere.exe was not found.");
        var start = new ProcessStartInfo(vswhere)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-products", "*", "-prerelease", "-version", "[18.0,19.0)", "-utf8", "-find", "Common7/IDE/devenv.exe" })
            start.ArgumentList.Add(argument);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        string output;
        try
        {
            using var query = Process.Start(start)!;
            output = await query.StandardOutput.ReadToEndAsync(timeout.Token);
            await query.WaitForExitAsync(timeout.Token);
            if (query.ExitCode != 0)
                throw new McpException($"VS_INSTALLATION_DISCOVERY_FAILED: vswhere exited with code {query.ExitCode}.");
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is Win32Exception or IOException or OperationCanceledException)
        {
            throw new McpException("VS_INSTALLATION_DISCOVERY_FAILED: Could not query Visual Studio Installer. " + error.Message);
        }

        var paths = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) throw new McpException("VS_INSTALLATION_NOT_FOUND: No installed Visual Studio 2026 IDE was found.");
        if (requestedPath != null)
            return paths.FirstOrDefault(path => string.Equals(path, Path.GetFullPath(requestedPath), StringComparison.OrdinalIgnoreCase))
                ?? throw new McpException("INVALID_DEVENV_PATH: Choose an installed Visual Studio 2026 devenv.exe from: " + string.Join(", ", paths));
        if (paths.Length != 1)
            throw new McpException("VS_INSTALLATION_AMBIGUOUS: Select an installation with devenvPath from: " + string.Join(", ", paths));
        return paths[0];
    }

    public static SafeProcessHandle Start(string devenvPath, string solutionPath, string? rootSuffix, out int pid)
    {
        const uint CreateBreakawayFromJob = 0x01000000;
        var command = new StringBuilder($"\"{devenvPath}\" \"{solutionPath}\"");
        if (rootSuffix != null) command.Append(" /RootSuffix ").Append(rootSuffix);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        // Keep the IDE out of the launcher's cleanup job and avoid inheriting MCP stdio.
        if (!CreateProcessW(devenvPath, command, IntPtr.Zero, IntPtr.Zero, false, CreateBreakawayFromJob,
            IntPtr.Zero, Path.GetDirectoryName(devenvPath)!, ref startup, out var process))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            throw new McpException($"VS_LAUNCH_FAILED: Could not start an independent Visual Studio process. Windows error {error.NativeErrorCode}: {error.Message}");
        }
        using var thread = new SafeWaitHandle(process.Thread, ownsHandle: true);
        pid = process.ProcessId;
        return new SafeProcessHandle(process.Process, ownsHandle: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
        string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
}
