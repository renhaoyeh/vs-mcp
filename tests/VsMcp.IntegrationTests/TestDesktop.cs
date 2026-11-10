#nullable enable
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VsMcp.IntegrationTests;

internal sealed class TestDesktop : IDisposable
{
    private const int CreateSuspended = 0x4;
    private const int CreateUnicodeEnvironment = 0x400;
    private const int CreateNoWindow = 0x08000000;
    public string Name { get; } = "VsMcpTests-" + Guid.NewGuid().ToString("N");
    private readonly DesktopHandle handle;

    public TestDesktop()
    {
        handle = CreateDesktop(Name, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the isolated test desktop. Tests will not fall back to the user's desktop.");
    }

    public Process Start(ProcessStartInfo info)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = Name, Flags = 1, ShowWindow = 0 };
        var environment = Marshal.StringToHGlobalUni(string.Join("\0", info.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value)) + "\0");
        try
        {
            // Suspend until Process owns a handle, so even an immediately exiting installer has a reliable exit code.
            if (!CreateProcess(info.FileName, new StringBuilder("\"" + info.FileName + "\" " + info.Arguments), IntPtr.Zero, IntPtr.Zero,
                false, CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow, environment,
                string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory, ref startup, out var created))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot start on the isolated test desktop: " + info.FileName);
            Process? process = null;
            try
            {
                process = Process.GetProcessById(created.ProcessId);
                process.EnableRaisingEvents = true;
                if (ResumeThread(created.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                return process;
            }
            catch
            {
                TerminateProcess(created.Process, 1);
                process?.Dispose();
                throw;
            }
            finally { CloseHandle(created.Thread); CloseHandle(created.Process); }
        }
        finally { Marshal.FreeHGlobal(environment); }
    }

    public void VerifyWindow(IntPtr window)
    {
        OnDesktop(() =>
        {
            var thread = GetWindowThreadProcessId(window, out _);
            if (thread == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the test window.");
            var actual = ObjectName(GetThreadDesktop(thread));
            if (actual != Name) throw new InvalidOperationException("The test window desktop is " + actual + ", expected " + Name + ".");
            return true;
        });
        VerifyNotInputDesktop();
    }

    public bool WindowIsVisible(IntPtr window) => OnDesktop(() => IsWindowVisible(window));

    private T OnDesktop<T>(Func<T> read)
    {
        // HWND lookups are scoped to the calling thread's desktop; this never switches the user's input desktop.
        var original = GetThreadDesktop(GetCurrentThreadId());
        if (!SetThreadDesktop(handle.DangerousGetHandle())) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return read(); }
        finally { if (!SetThreadDesktop(original)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }

    public void VerifyProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            foreach (ProcessThread thread in process.Threads)
            {
                var desktop = GetThreadDesktop((uint)thread.Id);
                if (desktop != IntPtr.Zero && ObjectName(desktop) != Name)
                    throw new InvalidOperationException("Test process " + pid + " has a thread outside the isolated desktop.");
            }
        }
        catch (ArgumentException) { /* a completed test process has no windows left to verify */ }
        VerifyNotInputDesktop();
    }

    private void VerifyNotInputDesktop()
    {
        using var input = OpenInputDesktop(0, false, 1);
        if (input.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (ObjectName(input.DangerousGetHandle()) == Name)
            throw new InvalidOperationException("The test desktop must never become the user's input desktop.");
    }

    private static string ObjectName(IntPtr desktop)
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * sizeof(char), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedData, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    private sealed class DesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DesktopHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseDesktop(handle);
    }

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern DesktopHandle CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, int access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern DesktopHandle OpenInputDesktop(int flags, bool inherit, int access);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        bool inherit, int flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, int exitCode);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
