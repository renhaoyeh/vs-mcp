using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsMcp.Contracts;
using VsMcp.Mcp;

namespace VsMcp.Services.Debugging
{
    // Passive SDM observer: never consumes stopping events, evaluates code or resumes a target.
    internal sealed class DebugEventObserver : IDebugEventCallback2, IDisposable
    {
        private sealed class ProgramEntry
        {
            public int? ProcessId;
            public string ProcessName;
            public string ProcessFileName;
            public string ProgramName;
            public IDebugProgram2 Program;
        }
        private readonly object gate = new object();
        private readonly Dictionary<Guid, ProgramEntry> programs = new Dictionary<Guid, ProgramEntry>();
        private readonly Dictionary<Guid, DebugObservedException> exceptions = new Dictionary<Guid, DebugObservedException>();
        private readonly IVsDebugger debugger;
        private readonly bool subscribed;
        private string error;
        public DebugEventObserver()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            debugger = ServiceProvider.GlobalProvider.GetService(typeof(SVsShellDebugger)) as IVsDebugger;
            if (debugger == null) { error = "The VS debugger service is unavailable."; return; }
            try
            {
                var hr = debugger.AdviseDebugEventCallback(this);
                subscribed = ErrorHandler.Succeeded(hr);
                if (ErrorHandler.Failed(hr)) error = "Debugger event subscription failed: 0x" + hr.ToString("X8");
            }
            catch (System.Runtime.InteropServices.COMException ex) { error = ex.Message; }
        }
        public int Event(IDebugEngine2 engine, IDebugProcess2 process, IDebugProgram2 program, IDebugThread2 thread, IDebugEvent2 ev, ref Guid iidEvent, uint attributes)
        {
            // SDM callbacks may arrive off the UI thread. Only debugger COM interfaces are
            // accessed here; DTE is read by tools after switching to the UI thread.
            try
            {
                if (program == null) return VSConstants.S_OK;
                ErrorHandler.ThrowOnFailure(program.GetProgramId(out var id));
                if (ev is IDebugProgramDestroyEvent2)
                {
                    lock (gate)
                    {
                        if (programs.TryGetValue(id, out var ending))
                        {
                            programs.Remove(id);
                            exceptions.Remove(id);
                        }
                    }
                    return VSConstants.S_OK;
                }
                if (process == null) ErrorHandler.ThrowOnFailure(program.GetProcess(out process));
                var physical = new AD_PROCESS_ID[1];
                ErrorHandler.ThrowOnFailure(process.GetPhysicalProcessId(physical));
                int? pid = physical[0].ProcessIdType == (uint)enum_AD_PROCESS_ID.AD_PROCESS_ID_SYSTEM ? (int?)checked((int)physical[0].dwProcessId) : null;
                process.GetName(enum_GETNAME_TYPE.GN_NAME, out var processName);
                process.GetName(enum_GETNAME_TYPE.GN_FILENAME, out var processFileName);
                program.GetName(out var programName);
                lock (gate)
                {
                    programs[id] = new ProgramEntry { ProcessId = pid, ProcessName = processName, ProcessFileName = processFileName, ProgramName = programName, Program = program };
                    if (ev is IDebugExceptionEvent2 exception)
                    {
                        var info = new EXCEPTION_INFO[1];
                        ErrorHandler.ThrowOnFailure(exception.GetException(info));
                        exception.GetExceptionDescription(out var description);
                        uint? tid = null;
                        if (thread != null && ErrorHandler.Succeeded(thread.GetThreadId(out var threadId))) tid = threadId;
                        exceptions[id] = new DebugObservedException
                        {
                            ProcessId = pid, ProgramId = id, ProcessGuid = pid.HasValue ? (Guid?)null : physical[0].guidProcessId,
                            ThreadId = tid, Name = info[0].bstrExceptionName,
                            Code = "0x" + info[0].dwCode.ToString("X8"), Description = description,
                            Category = info[0].guidType, State = info[0].dwState.ToString(), ObservedAtUtc = DateTime.UtcNow,
                        };
                    }
                }
            }
            catch (Exception ex) { lock (gate) error = ex.Message; }
            return VSConstants.S_OK;
        }
        private KeyValuePair<Guid, ProgramEntry>[] Select(int pid, string processName, string programName)
        {
            var numeric = programs.Where(p => p.Value.ProcessId.HasValue && p.Value.ProcessId.Value == pid).ToArray();
            if (numeric.Length != 0) return numeric;
            // Dump engines may identify a recorded process by GUID rather than an OS PID.
            // Use both exact process and program names, and reject ambiguous matches.
            var named = programs.Where(p => !p.Value.ProcessId.HasValue &&
                (string.Equals(p.Value.ProcessName, processName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(p.Value.ProcessFileName, processName, StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(p.Value.ProgramName, programName, StringComparison.Ordinal)).ToArray();
            return named.Length == 1 ? named : new KeyValuePair<Guid, ProgramEntry>[0];
        }
        public DebugExceptionOutput Exception(int pid, string processName, string programName)
        {
            lock (gate) return new DebugExceptionOutput
            {
                ProcessId = pid,
                LastObservedException = Select(pid, processName, programName).Where(p => exceptions.ContainsKey(p.Key))
                    .Select(p => exceptions[p.Key]).OrderByDescending(e => e.ObservedAtUtc).FirstOrDefault(),
                ObservationError = error,
            };
        }
        public DebugModulesOutput Modules(int pid, string processName, string programName, int maximum)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            KeyValuePair<Guid, ProgramEntry>[] selected;
            lock (gate) selected = Select(pid, processName, programName);
            if (selected.Length == 0)
                throw new McpToolException(ToolErrorCode.NotSupported, "No debug program was observed for the current process. Start or attach a new session with this extension loaded. " + error);
            var result = new DebugModulesOutput { ProcessId = pid };
            foreach (var entry in selected)
            {
                ErrorHandler.ThrowOnFailure(entry.Value.Program.EnumModules(out var enumerator));
                var one = new IDebugModule2[1];
                while (true)
                {
                    uint fetched = 0;
                    var hr = enumerator.Next(1, one, ref fetched);
                    ErrorHandler.ThrowOnFailure(hr);
                    if (fetched == 0) break;
                    if (result.Items.Count == maximum) { result.Truncated = true; return result; }
                    var item = new DebugModuleInfo { ProgramId = entry.Key };
                    var data = new MODULE_INFO[1];
                    const enum_MODULE_INFO_FIELDS fields = enum_MODULE_INFO_FIELDS.MIF_NAME | enum_MODULE_INFO_FIELDS.MIF_URL |
                        enum_MODULE_INFO_FIELDS.MIF_VERSION | enum_MODULE_INFO_FIELDS.MIF_DEBUGMESSAGE |
                        enum_MODULE_INFO_FIELDS.MIF_LOADADDRESS | enum_MODULE_INFO_FIELDS.MIF_SIZE |
                        enum_MODULE_INFO_FIELDS.MIF_TIMESTAMP | enum_MODULE_INFO_FIELDS.MIF_URLSYMBOLLOCATION | enum_MODULE_INFO_FIELDS.MIF_FLAGS;
                    hr = one[0].GetInfo(fields, data);
                    if (ErrorHandler.Failed(hr)) item.Error = "GetInfo: 0x" + hr.ToString("X8");
                    else
                    {
                        var m = data[0];
                        bool Has(enum_MODULE_INFO_FIELDS flag) => (m.dwValidFields & flag) != 0;
                        item.Name = Has(enum_MODULE_INFO_FIELDS.MIF_NAME) ? m.m_bstrName : null;
                        item.Path = Has(enum_MODULE_INFO_FIELDS.MIF_URL) ? m.m_bstrUrl : null;
                        item.Version = Has(enum_MODULE_INFO_FIELDS.MIF_VERSION) ? m.m_bstrVersion : null;
                        item.SymbolPath = Has(enum_MODULE_INFO_FIELDS.MIF_URLSYMBOLLOCATION) ? m.m_bstrUrlSymbolLocation : null;
                        item.SymbolStatus = Has(enum_MODULE_INFO_FIELDS.MIF_DEBUGMESSAGE) ? m.m_bstrDebugMessage : null;
                        item.LoadAddress = Has(enum_MODULE_INFO_FIELDS.MIF_LOADADDRESS) ? "0x" + m.m_addrLoadAddress.ToString("X") : null;
                        item.Size = Has(enum_MODULE_INFO_FIELDS.MIF_SIZE) ? (uint?)m.m_dwSize : null;
                        item.Flags = Has(enum_MODULE_INFO_FIELDS.MIF_FLAGS) ? m.m_dwModuleFlags.ToString() : null;
                        if (Has(enum_MODULE_INFO_FIELDS.MIF_TIMESTAMP))
                        {
                            var stamp = ((long)(uint)m.m_TimeStamp.dwHighDateTime << 32) | (uint)m.m_TimeStamp.dwLowDateTime;
                            try { if (stamp > 0) item.TimestampUtc = DateTime.FromFileTimeUtc(stamp); }
                            catch (ArgumentOutOfRangeException) { item.Error = "The engine returned an invalid timestamp."; }
                        }
                    }
                    result.Items.Add(item);
                }
            }
            return result;
        }
        internal IDebugProgram2 GetProgram(int pid, string processName, string programName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            KeyValuePair<Guid, ProgramEntry>[] selected;
            lock (gate) selected = Select(pid, processName, programName);
            if (selected.Length == 0)
                throw new McpToolException(ToolErrorCode.NotSupported, "No exact observed debug program is available for the current process. Start or attach a new session with this extension loaded. " + error);
            if (selected.Length != 1)
                throw new McpToolException(ToolErrorCode.NotSupported, "The current process maps to multiple observed debug programs; native inspection requires an unambiguous program.");
            return selected[0].Value.Program;
        }
        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { if (subscribed) debugger.UnadviseDebugEventCallback(this); }
            catch (System.Runtime.InteropServices.COMException) { /* Host debugger is shutting down. */ }
            lock (gate) { programs.Clear(); exceptions.Clear(); }
        }
    }
}
