using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using VsMcp.Contracts;
using VsMcp.Mcp;
using VsMcp.Services.Build;
using VsMcp.Services.VisualStudio;

namespace VsMcp.Tools
{
    internal sealed class DebugDiagnosticsTools
    {
        private readonly VsContext vs;
        public DebugDiagnosticsTools(VsContext vs) { this.vs = vs; }

        [McpServerTool(Name = "debugger_open_dump", Title = "Debug a dump in Visual Studio", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
        [Description("Request debugging of an existing Windows user-mode minidump in the connected idle VS via its native debugger API. Requires an absolute .dmp/.mdmp/.hdmp path and MDMP header. engineGuid defaults to the VS native-code engine; an explicit installed dump-capable engine GUID can select another engine. This is a debug-session mutation, not offline analysis: VS may display UI, load symbols or use configured symbol servers. Does not run F5, build, change the solution/configuration/startup projects, search WER or invoke cdb. Returns request acceptance, not successful dump loading; inspect get_debug_status.request and dumpRequest until break/failed/ended. Once paused, use debugger_get_exception, debugger_list_modules, debugger_get_callstack, debugger_list_threads and debugger_get_locals. stop_debugging ends the dump session. After interruption inspect status; never blindly retry.")]
        public async Task<CallToolResult> OpenDumpAsync(string path, string engineGuid = null, CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            IdleOrPaused(false);
            path = AbsolutePath(path);
            if (!new[] { ".dmp", ".mdmp", ".hdmp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw Error(ToolErrorCode.InvalidArgument, "Expected a .dmp, .mdmp or .hdmp file.");
            var engine = new Guid("3b476d35-a401-11d2-aad4-00c04f990171");
            if (engineGuid != null && (!Guid.TryParse(engineGuid, out engine) || engine == Guid.Empty))
                throw Error(ToolErrorCode.InvalidArgument, "engineGuid must be a non-empty debugger engine GUID.");
            // Keep the verified file locked against replacement until VS's launch API returns.
            FileStream lease = null;
            try
            {
                lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (lease.Length < 32 || lease.ReadByte() != 'M' || lease.ReadByte() != 'D' || lease.ReadByte() != 'M' || lease.ReadByte() != 'P')
                    throw Error(ToolErrorCode.InvalidArgument, "The file does not contain a Windows minidump header.");
                cancellationToken.ThrowIfCancellationRequested();
                var result = vs.Debug().OpenDump(vs.Instance(), path, engine, lease);
                lease = null; // The queued operation now owns the file lease.
                return ToolJson.Result(result);
            }
            catch (IOException ex) { throw Error(ToolErrorCode.InvalidArgument, "Cannot read the dump: " + ex.Message); }
            finally { lease?.Dispose(); }
        }

        [McpServerTool(Name = "debugger_save_dump", Title = "Save current debug process dump", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
        [Description("Save a normal or full dump of the currently selected paused debug process using VS WriteMinidump. Requires processId to match the current debugger process and an absolute new .dmp path in an existing directory. Never overwrites an existing target. Writes to a unique temporary dump then moves it to the target; a failed write/move preserves the temporary dump and reports its path. Full dumps can be large. This captures the current paused state, not necessarily a crash. Does not resume, attach or invoke external dump tools. Engine support varies. After interruption inspect the target/temporary files before retrying.")]
        public async Task<DebugDumpSaved> SaveDumpAsync(string path, int processId, DebugDumpKind kind = DebugDumpKind.Normal, CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Paused();
            path = AbsolutePath(path);
            if (!Enum.IsDefined(typeof(DebugDumpKind), kind) || !string.Equals(Path.GetExtension(path), ".dmp", StringComparison.OrdinalIgnoreCase))
                throw Error(ToolErrorCode.InvalidArgument, "Use a .dmp target and normal/full kind.");
            if (processId != vs.Dte.Debugger.CurrentProcess.ProcessID || processId <= 0)
                throw Error(ToolErrorCode.InvalidArgument, "processId must match the currently selected paused debug process.");
            if (File.Exists(path) || Directory.Exists(path) || !Directory.Exists(Path.GetDirectoryName(path)))
                throw Error(ToolErrorCode.InvalidArgument, "The target must be a new file in an existing directory.");
            var temporary = Path.Combine(Path.GetDirectoryName(path), ".vs-mcp-" + Guid.NewGuid().ToString("N") + ".dmp");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ((EnvDTE90a.Debugger4)vs.Dte.Debugger).WriteMinidump(temporary,
                    kind == DebugDumpKind.Full ? dbgMinidumpOption.dbgMinidumpFull : dbgMinidumpOption.dbgMinidumpNormal);
                var file = new FileInfo(temporary);
                if (!file.Exists || file.Length < 32) throw new IOException("The debug engine did not produce a dump.");
                File.Move(temporary, path);
                file = new FileInfo(path);
                return new DebugDumpSaved { Instance = vs.ReadInstance(), Path = path, Kind = kind, ProcessId = processId, Length = file.Length, LastWriteTimeUtc = file.LastWriteTimeUtc };
            }
            catch (Exception ex)
            {
                throw Error(ToolErrorCode.ExecutionError, "Dump save was not confirmed. Inspect files before retrying. " + ex.Message,
                    new JObject { ["path"] = path, ["temporaryPath"] = temporary, ["temporaryExists"] = File.Exists(temporary), ["targetExists"] = File.Exists(path) });
            }
        }

        [McpServerTool(Name = "debugger_list_modules", Title = "List debugger modules", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Read bounded modules for the currently selected paused process, including dump sessions, via observed debug programs or the exact current Process3.Modules automation view. Reports VS engine-supplied paths, versions, symbol status/location, addresses and timestamps; fields unavailable through the selected provider are null and per-module failures have error. Does not read newer local binaries as a substitute, force symbol loading or change selection. An engine timestamp is not proof of source/build identity. Unsupported enumeration fails explicitly.")]
        public async Task<DebugModulesOutput> ModulesAsync([Range(1, 5000)] int maxModules = 200, CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Paused();
            if (maxModules < 1 || maxModules > 5000) throw Error(ToolErrorCode.InvalidArgument, "maxModules must be between 1 and 5000.");
            var debugger = vs.Dte.Debugger;
            DebugModulesOutput result;
            try { result = vs.DebugEvents.Modules(debugger.CurrentProcess.ProcessID, debugger.CurrentProcess.Name, debugger.CurrentProgram?.Name, maxModules); }
            catch (McpToolException ex) when (ex.Code == ToolErrorCode.NotSupported)
            {
                // Process3.Modules is a direct VS automation view of this exact current process;
                // it also works for dump engines without numeric OS process IDs.
                if (!(debugger.CurrentProcess is EnvDTE90.Process3 process)) throw Error(ToolErrorCode.NotSupported, ex.Message);
                var modules = process.Modules;
                result = new DebugModulesOutput { ProcessId = process.ProcessID, Truncated = modules.Count > maxModules };
                foreach (EnvDTE90.Module module in modules.Cast<EnvDTE90.Module>().Take(maxModules))
                {
                    var item = new DebugModuleInfo();
                    try
                    {
                        item.Name = module.Name; item.Path = module.Path; item.Version = module.Version;
                        item.SymbolPath = module.SymbolFile; item.LoadAddress = "0x" + module.LoadAddress.ToString("X");
                    }
                    catch (System.Runtime.InteropServices.COMException error) { item.Error = error.Message; }
                    result.Items.Add(item);
                }
            }
            result.Instance = vs.ReadInstance();
            return result;
        }

        [McpServerTool(Name = "debugger_get_exception", Title = "Read observed debugger exception", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Read the last exception event observed from the VS debug engine for the currently selected paused process, with exception code/name/description, event thread, state and observation time plus the current break reason/thread. Works for live and dump sessions when their engine emits exception events. Null means none observed; it does not mean no exception/crash occurred. An observed event is not proof of a crash or the cause of the current stop. Use debugger_get_callstack and debugger_list_threads to inspect context; this tool never evaluates expressions, resumes or changes exception settings.")]
        public async Task<DebugExceptionOutput> ExceptionAsync(CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Paused();
            var debugger = vs.Dte.Debugger;
            var result = vs.DebugEvents.Exception(debugger.CurrentProcess.ProcessID, debugger.CurrentProcess.Name, debugger.CurrentProgram?.Name);
            result.Instance = vs.ReadInstance();
            result.CurrentThreadId = debugger.CurrentThread == null ? (uint?)null : checked((uint)debugger.CurrentThread.ID);
            result.BreakReason = debugger.LastBreakReason.ToString();
            return result;
        }

        [McpServerTool(Name = "debugger_get_symbol_settings", Title = "Read debugger symbol settings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Read the connected VS debugger's persistent PDB search paths, enabled-path mask, cache path and manual-loading preference through Debugger4. Does not load symbols, access symbol servers or change a debug session. Available without a solution.")]
        public async Task<DebugSymbolSettingsOutput> GetSymbolsAsync(CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Symbols();
        }

        [McpServerTool(Name = "debugger_set_symbol_settings", Title = "Update debugger symbol settings", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
        [Description("Patch the connected idle or paused VS debugger's persistent PDB settings through Debugger4.SetSymbolSettings. Specify at least one field or loadSymbolsNow. If changing symbolPath, also pass symbolPathState: one character per semicolon-separated path, 1 enabled and other characters disabled; empty strings clear the path/mask. Omitted fields retain their current values. Returns previous and read-back settings. loadSymbolsNow requires a paused session, may block while VS searches configured local paths/symbol servers and is only a loading request, not proof every module found matching PDBs; inspect debugger_list_modules afterward. No automatic restore. After interruption/error read settings and module status before retrying. Does not alter active configuration or startup projects.")]
        public async Task<DebugSymbolSettingsOutput> SetSymbolsAsync(string symbolPath = null, string symbolPathState = null,
            string symbolCachePath = null, bool? onlyLoadSymbolsManually = null, bool loadSymbolsNow = false, CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            IdleOrPaused(true);
            if (loadSymbolsNow) Paused();
            if (symbolPath == null && symbolPathState == null && symbolCachePath == null && !onlyLoadSymbolsManually.HasValue && !loadSymbolsNow)
                throw Error(ToolErrorCode.InvalidArgument, "Specify a symbol setting or loadSymbolsNow.");
            if ((symbolPath == null) != (symbolPathState == null))
                throw Error(ToolErrorCode.InvalidArgument, "Supply symbolPath and symbolPathState together.");
            if (symbolPath != null && (symbolPathState.Length != (symbolPath.Length == 0 ? 0 : symbolPath.Split(';').Length) ||
                symbolPathState.Any(char.IsControl) || symbolPath.Contains("\n") || symbolPath.Contains("\r")))
                throw Error(ToolErrorCode.InvalidArgument, "symbolPathState must contain one non-control character per semicolon-separated symbol path.");
            if (symbolCachePath != null && symbolCachePath.Length != 0 && (!Path.IsPathRooted(symbolCachePath) || Path.GetPathRoot(symbolCachePath).Length < 3))
                throw Error(ToolErrorCode.InvalidArgument, "symbolCachePath must be empty or absolute.");
            var previous = Symbols();
            var path = symbolPath ?? previous.SymbolPath;
            var mask = symbolPathState ?? previous.SymbolPathState;
            var cache = symbolCachePath ?? previous.SymbolCachePath;
            var manual = onlyLoadSymbolsManually ?? previous.OnlyLoadSymbolsManually;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ((EnvDTE90a.Debugger4)vs.Dte.Debugger).SetSymbolSettings(path, mask, cache, manual, loadSymbolsNow);
                var actual = Symbols();
                if (actual.SymbolPath != path || actual.SymbolPathState != mask || actual.SymbolCachePath != cache || actual.OnlyLoadSymbolsManually != manual)
                    throw new InvalidOperationException("Visual Studio did not retain every requested symbol setting.");
                actual.Previous = previous;
                actual.LoadSymbolsRequested = loadSymbolsNow;
                return actual;
            }
            catch (Exception ex)
            {
                throw Error(ToolErrorCode.ExecutionError, "Symbol update was not confirmed. Read settings and module status before retrying. " + ex.Message,
                    new JObject { ["previous"] = ToolJson.ToJObject(previous) });
            }
        }
        private DebugSymbolSettingsOutput Symbols()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = vs.Dte.Debugger as EnvDTE90a.Debugger4;
            if (debugger == null) throw Error(ToolErrorCode.NotSupported, "This host does not expose Debugger4 symbol settings.");
            return new DebugSymbolSettingsOutput
            {
                Instance = vs.ReadInstance(), SymbolPath = debugger.SymbolPath, SymbolPathState = debugger.SymbolPathState,
                SymbolCachePath = debugger.SymbolCachePath, OnlyLoadSymbolsManually = debugger.OnlyLoadSymbolsManually,
            };
        }

        private string AbsolutePath(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || Path.GetPathRoot(path).Length < 3)
                throw Error(ToolErrorCode.InvalidArgument, "An absolute path is required.");
            return Path.GetFullPath(path);
        }
        private void Paused()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (vs.Dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode || vs.Dte.Debugger.CurrentProcess == null)
                throw Error(ToolErrorCode.ExecutionError, "The debugger must be paused with a current process.");
        }
        private void IdleOrPaused(bool allowPaused)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var mode = vs.Dte.Debugger.CurrentMode;
            if (BuildRunner.IsRunning || BuildRunner.IsBuildManagerBusy() || vs.TestsRunning() || vs.DebugLaunchPending ||
                (mode != dbgDebugMode.dbgDesignMode && !(allowPaused && mode == dbgDebugMode.dbgBreakMode)))
                throw Error(ToolErrorCode.ExecutionError, "Wait for builds, tests, debugging and pending launches to finish.");
        }
        private McpToolException Error(ToolErrorCode code, string message, JObject details = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            details = details ?? new JObject();
            details["instance"] = vs.Instance();
            return new McpToolException(code, message, details);
        }
    }
}
