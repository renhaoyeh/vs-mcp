using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VsMcp.Infrastructure
{
    // A private Windows job bounds cancellation to this runner and its child test hosts.
    internal sealed class OwnedProcessJob : SafeHandleZeroOrMinusOneIsInvalid
    {
        public OwnedProcessJob() : base(true)
        {
            SetHandle(CreateJobObject(IntPtr.Zero, null));
            if (IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(limits))) { var error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
        }
        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception();
        }
        public void Terminate() { if (!IsClosed && !IsInvalid) TerminateJobObject(handle, 1); }
        protected override bool ReleaseHandle() => CloseHandle(handle);

        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimits information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
