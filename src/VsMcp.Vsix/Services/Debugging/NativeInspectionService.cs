using System;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using VsMcp.Contracts;
using VsMcp.Mcp;
using VsMcp.Services.VisualStudio;

namespace VsMcp.Services.Debugging
{
    internal sealed class NativeInspectionService
    {
        private static readonly Guid RegisterFilter = new Guid("223AE797-BD09-4F28-BBAB-37D0ED7521D6");
        private static readonly Guid LocalsFilter = new Guid("B200F725-E725-11D0-B881-00A0244A1DE2");
        private readonly VsContext vs;

        public NativeInspectionService(VsContext context) { vs = context; }

        public NativeDebuggerInventory Inventory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = vs.Dte.Debugger as Debugger2;
            if (debugger == null) throw Error(ToolErrorCode.NotSupported, "Debugger2 transport and engine enumeration is unavailable.");
            var result = new NativeDebuggerInventory { Instance = vs.ReadInstance() };
            foreach (Transport transport in debugger.Transports)
            {
                var item = new NativeTransportInfo { Name = transport.Name, Id = transport.ID };
                foreach (Engine engine in transport.Engines)
                    item.Engines.Add(new NativeEngineInfo { Name = engine.Name, Id = engine.ID, AttachResult = engine.AttachResult.ToString() });
                result.Transports.Add(item);
            }
            return result;
        }

        public NativeSymbolsOutput LoadSymbols(int processId, string moduleName, string modulePath, string pdbPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var current = Paused(processId);
            if (!(current.Process is EnvDTE90.Process3 process)) throw Error(ToolErrorCode.NotSupported, "The current process does not expose Process3.Modules.");
            if (string.IsNullOrWhiteSpace(moduleName)) throw Error(ToolErrorCode.InvalidArgument, "An exact module name is required.");
            if (modulePath != null && (!System.IO.Path.IsPathRooted(modulePath) || System.IO.Path.GetPathRoot(modulePath).Length < 3))
                throw Error(ToolErrorCode.InvalidArgument, "modulePath must be an absolute loaded-module path when supplied.");
            if (pdbPath != null)
            {
                if (string.IsNullOrWhiteSpace(pdbPath) || !System.IO.Path.IsPathRooted(pdbPath) || System.IO.Path.GetPathRoot(pdbPath).Length < 3)
                    throw Error(ToolErrorCode.InvalidArgument, "pdbPath must be an absolute path when supplied.");
                pdbPath = System.IO.Path.GetFullPath(pdbPath);
                if (!System.IO.File.Exists(pdbPath)) throw Error(ToolErrorCode.InvalidArgument, "The requested PDB file does not exist.");
            }
            var modulesCollection = process.Modules;
            var modules = new EnvDTE90.Module[modulesCollection.Count];
            for (var index = 1; index <= modules.Length; index++)
                modules[index - 1] = (EnvDTE90.Module)modulesCollection.Item(index);
            var matches = modules.Where(m =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase)) return false;
                return modulePath == null || string.Equals(Normalize(m.Path), Normalize(modulePath), StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            if (matches.Length == 0) throw Error(ToolErrorCode.InvalidArgument, "No module in the exact current Process3.Modules collection matched moduleName and modulePath.");
            if (matches.Length > 1) throw Error(ToolErrorCode.InvalidArgument, "The module name matched multiple loaded modules; supply modulePath to select exactly one.");

            var module = matches[0];
            var selectedName = module.Name;
            var selectedPath = module.Path;
            // VS may keep already-visible symbols and ignore this path. Read SymbolFile back either way.
            var suppressUi = vs.Dte.SuppressUI;
            try
            {
                vs.Dte.SuppressUI = true;
                module.LoadSymbols(pdbPath ?? string.Empty);
            }
            catch (COMException ex) { throw Error(ToolErrorCode.ExecutionError, "Visual Studio did not confirm the module symbol-load request: " + ex.Message); }
            finally { vs.Dte.SuppressUI = suppressUi; }
            var actual = module.SymbolFile;
            return new NativeSymbolsOutput
            {
                Instance = vs.ReadInstance(), ContextId = current.ContextId, ProcessId = processId,
                ModuleName = selectedName, ModulePath = selectedPath, RequestedPdbPath = pdbPath,
                ActualSymbolFile = string.IsNullOrWhiteSpace(actual) ? null : actual, LoadRequested = true,
                Note = "Visual Studio accepted the per-module LoadSymbols request and SymbolFile was read back. The requested PDB path may be ignored when symbols are already visible on the configured search path; readback does not prove the requested PDB was used or matches the loaded module.",
            };
        }

        public NativeMemoryOutput ReadMemory(int processId, string variableName, string address, int byteCount, int frameIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var current = Paused(processId);
            ValidateFrame(frameIndex);
            bool byVariable = !string.IsNullOrWhiteSpace(variableName);
            bool byAddress = !string.IsNullOrWhiteSpace(address);
            if (byVariable == byAddress) throw Error(ToolErrorCode.InvalidArgument, "Supply exactly one of variableName or address.");
            if (byteCount < 1 || byteCount > 4096) throw Error(ToolErrorCode.InvalidArgument, "byteCount must be between 1 and 4096.");
            IDebugStackFrame2 frame;
            try { frame = Frame(current.Program, checked((uint)current.ThreadId), frameIndex); }
            catch (COMException ex) { throw Error(ToolErrorCode.NotSupported, "The engine could not expose the requested paused stack frame: " + ex.Message); }
            IDebugMemoryContext2 memoryContext;
            string resolvedAddress = null;
            if (byVariable)
            {
                memoryContext = null;
                try
                {
                    var property = FindLocal(frame, variableName);
                    var contextHr = property.GetMemoryContext(out memoryContext);
                    if (ErrorHandler.Failed(contextHr)) memoryContext = null;
                }
                catch (McpToolException ex) when (ex.Code == ToolErrorCode.NotSupported) { }
                if (memoryContext == null)
                {
                    var property = EvaluateLocalAddress(frame, variableName);
                    var contextHr = property.GetMemoryContext(out memoryContext);
                    if (ErrorHandler.Failed(contextHr) || memoryContext == null)
                        throw Error(ToolErrorCode.NotSupported, "The native expression result does not expose a memory context for this local (HRESULT 0x" + contextHr.ToString("X8") + ").");
                }
            }
            else
            {
                if (!TryHexAddress(address, out var targetAddress)) throw Error(ToolErrorCode.InvalidArgument, "address must be a hexadecimal address such as 0x7FF600001000.");
                memoryContext = MemoryContextAtAddress(frame, address);
            }
            IDebugMemoryBytes2 memory;
            var memoryHr = current.Program.GetMemoryBytes(out memory);
            if (ErrorHandler.Failed(memoryHr) || memory == null)
                throw Error(ToolErrorCode.NotSupported, "The debug engine does not expose IDebugProgram2.GetMemoryBytes for this target.");
            var buffer = new byte[byteCount];
            uint read = 0, unreadable = 0;
            var hr = memory.ReadAt(memoryContext, checked((uint)byteCount), buffer, out read, ref unreadable);
            if (ErrorHandler.Failed(hr) && read == 0)
                throw Error(ToolErrorCode.ExecutionError, "The debug engine could not read this variable's memory: 0x" + hr.ToString("X8"));
            var bytes = buffer.Take((int)Math.Min(read, (uint)buffer.Length)).ToArray();
            return new NativeMemoryOutput
            {
                Instance = vs.ReadInstance(), ContextId = current.ContextId, ProcessId = processId,
                ThreadId = current.ThreadId, FrameIndex = frameIndex,
                VariableName = byVariable ? variableName : null, Address = byAddress ? address : resolvedAddress, RequestedBytes = byteCount,
                BytesRead = bytes.Length, HexBytes = BitConverter.ToString(bytes).Replace("-", " "),
                Partial = bytes.Length < byteCount || unreadable != 0 || ErrorHandler.Failed(hr),
                UnreadableBytes = unreadable == 0 ? (uint?)null : unreadable,
                Error = ErrorHandler.Failed(hr) ? "ReadAt returned 0x" + hr.ToString("X8") :
                    (unreadable != 0 ? "Some bytes were unmapped or inaccessible." : (read < byteCount ? "The engine returned fewer bytes than requested." : null)),
            };
        }

        public NativeDisassemblyOutput Disassemble(int processId, int frameIndex, int maxInstructions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var current = Paused(processId);
            ValidateFrame(frameIndex);
            if (maxInstructions < 1 || maxInstructions > 256) throw Error(ToolErrorCode.InvalidArgument, "maxInstructions must be between 1 and 256.");
            IDebugStackFrame2 frame;
            try { frame = Frame(current.Program, checked((uint)current.ThreadId), frameIndex); }
            catch (COMException ex) { throw Error(ToolErrorCode.NotSupported, "The engine could not expose the requested paused stack frame: " + ex.Message); }
            IDebugCodeContext2 code;
            var codeHr = frame.GetCodeContext(out code);
            if (ErrorHandler.Failed(codeHr)) throw Error(ToolErrorCode.NotSupported, "The selected stack frame has no code context (HRESULT 0x" + codeHr.ToString("X8") + ").");
            if (code == null) throw Error(ToolErrorCode.NotSupported, "The selected stack frame has no code context.");
            IDebugDisassemblyStream2 stream;
            int streamHr;
            try { streamHr = current.Program.GetDisassemblyStream(enum_DISASSEMBLY_STREAM_SCOPE.DSS_FUNCTION, code, out stream); }
            catch (COMException ex) { throw Error(ToolErrorCode.NotSupported, "The debug engine does not provide a disassembly stream for the selected frame: " + ex.Message); }
            if (ErrorHandler.Failed(streamHr) || stream == null)
                throw Error(ToolErrorCode.NotSupported, "The debug engine does not provide a disassembly stream for the selected frame.");
            var seekHr = stream.Seek(enum_SEEK_START.SEEK_START_CODECONTEXT, code, 0, 0);
            if (ErrorHandler.Failed(seekHr)) throw Error(ToolErrorCode.NotSupported, "The engine could not seek its disassembly stream to the current code context (HRESULT 0x" + seekHr.ToString("X8") + ").");
            var data = new DisassemblyData[maxInstructions];
            uint read = 0;
            const enum_DISASSEMBLY_STREAM_FIELDS fields = enum_DISASSEMBLY_STREAM_FIELDS.DSF_ADDRESS |
                enum_DISASSEMBLY_STREAM_FIELDS.DSF_CODEBYTES | enum_DISASSEMBLY_STREAM_FIELDS.DSF_OPCODE |
                enum_DISASSEMBLY_STREAM_FIELDS.DSF_OPERANDS | enum_DISASSEMBLY_STREAM_FIELDS.DSF_SYMBOL;
            var hr = stream.Read(checked((uint)maxInstructions), fields, out read, data);
            if (ErrorHandler.Failed(hr) && read == 0)
                throw Error(ToolErrorCode.ExecutionError, "The debug engine could not read disassembly: 0x" + hr.ToString("X8"));
            var result = new NativeDisassemblyOutput
            {
                Instance = vs.ReadInstance(), ContextId = current.ContextId, ProcessId = processId,
                ThreadId = current.ThreadId, FrameIndex = frameIndex, RequestedInstructions = maxInstructions,
                Truncated = read >= maxInstructions,
            };
            for (var i = 0; i < Math.Min((int)read, data.Length); i++)
                result.Instructions.Add(new NativeInstruction
                {
                    Address = data[i].bstrAddress, Bytes = data[i].bstrCodeBytes,
                    Opcode = data[i].bstrOpcode, Operands = data[i].bstrOperands, Symbol = data[i].bstrSymbol,
                });
            return result;
        }

        public NativeRegistersOutput Registers(int processId, int frameIndex, int maxRegisters)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var current = Paused(processId);
            ValidateFrame(frameIndex);
            if (maxRegisters < 1 || maxRegisters > 256) throw Error(ToolErrorCode.InvalidArgument, "maxRegisters must be between 1 and 256.");
            var result = new NativeRegistersOutput
            {
                Instance = vs.ReadInstance(), ContextId = current.ContextId,
                ProcessId = processId, ThreadId = current.ThreadId, FrameIndex = frameIndex,
            };
            IDebugStackFrame2 frame;
            try { frame = Frame(current.Program, checked((uint)current.ThreadId), frameIndex); }
            catch (COMException ex) { result.UnsupportedReason = "The debug engine could not expose the selected stack frame: " + ex.Message; return result; }
            IEnumDebugPropertyInfo2 enumerator;
            uint count = 0;
            var filter = RegisterFilter;
            const enum_DEBUGPROP_INFO_FLAGS fields = enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_NAME |
                enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_TYPE | enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_VALUE |
                enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_ATTRIB | enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_PROP |
                enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_NOFUNCEVAL;
            int hr;
            try { hr = frame.EnumProperties(fields, 16, ref filter, 1000, out count, out enumerator); }
            catch (COMException)
            {
                hr = VSConstants.E_NOTIMPL;
                enumerator = null;
            }
            if (ErrorHandler.Failed(hr) || enumerator == null)
            {
                IDebugProperty2 frameProperties;
                try { hr = frame.GetDebugProperty(out frameProperties); }
                catch (COMException) { hr = VSConstants.E_NOTIMPL; frameProperties = null; }
                if (ErrorHandler.Succeeded(hr) && frameProperties != null)
                {
                    filter = RegisterFilter;
                    try { hr = frameProperties.EnumChildren(fields, 16, ref filter, enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_NONE, null, 1000, out enumerator); }
                    catch (COMException) { hr = VSConstants.E_NOTIMPL; enumerator = null; }
                    if (ErrorHandler.Succeeded(hr) && enumerator != null)
                    {
                        hr = enumerator.GetCount(out count);
                    }
                }
                if (ErrorHandler.Failed(hr) || enumerator == null)
                {
                    if (ReadNativeRegistersFromFrame(frame, result, maxRegisters)) return result;
                    result.UnsupportedReason = "This engine supports neither AD7 register properties nor Visual Studio's fixed native register expressions (HRESULT 0x" + hr.ToString("X8") + ").";
                    return result;
                }
            }
            var infos = new DEBUG_PROPERTY_INFO[Math.Min((int)Math.Min(count, (uint)maxRegisters), maxRegisters)];
            uint fetched = 0;
            if (infos.Length != 0)
            {
                hr = enumerator.Next(checked((uint)infos.Length), infos, out fetched);
                if (ErrorHandler.Failed(hr) && fetched == 0)
                {
                    result.UnsupportedReason = "The engine rejected register enumeration (HRESULT 0x" + hr.ToString("X8") + ").";
                    return result;
                }
            }
            for (var i = 0; i < Math.Min((int)fetched, infos.Length); i++)
            {
                if (infos[i].dwAttrib.ToString().IndexOf("DBG_ATTRIB_STORAGE_REGISTER", StringComparison.Ordinal) < 0) continue;
                result.Registers.Add(new NativeRegisterInfo
                {
                    Name = infos[i].bstrName, Type = infos[i].bstrType, Value = infos[i].bstrValue,
                    Attributes = infos[i].dwAttrib.ToString(),
                });
            }
            if (result.Registers.Count == 0 && ReadNativeRegistersFromFrame(frame, result, maxRegisters)) return result;
            result.Supported = true;
            result.Truncated = count > (uint)maxRegisters || fetched < count;
            if (result.Registers.Count == 0)
            {
                result.Supported = false;
                result.UnsupportedReason = "The engine returned no scalar CPU registers for the selected frame.";
            }
            return result;
        }

        private bool ReadNativeRegistersFromFrame(IDebugStackFrame2 frame, NativeRegistersOutput output, int maximum)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            IDebugExpressionContext2 context;
            var contextHr = frame.GetExpressionContext(out context);
            if (ErrorHandler.Failed(contextHr) || context == null)
            {
                output.UnsupportedReason = "The engine does not expose an expression context for the selected stack frame (HRESULT 0x" + contextHr.ToString("X8") + ").";
                return false;
            }
            var wide = new[] { "RAX", "RBX", "RCX", "RDX", "RSI", "RDI", "RSP", "RBP", "R8", "R9", "R10", "R11", "R12", "R13", "R14", "R15", "RIP", "EFLAGS" };
            if (!EvaluateRegisters(context, wide, output, maximum))
            {
                var narrow = new[] { "EAX", "EBX", "ECX", "EDX", "ESI", "EDI", "ESP", "EBP", "EIP", "EFLAGS" };
                EvaluateRegisters(context, narrow, output, maximum);
            }
            if (output.Registers.Count == 0)
            {
                output.Supported = false;
                output.UnsupportedReason = "The selected debugger engine exposes neither AD7 register properties nor any allowlisted native frame register expressions.";
                return false;
            }
            output.Supported = true;
            output.Truncated = output.Registers.Count >= maximum;
            output.Backend = "Selected IDebugStackFrame2 expression context with a fixed native register allowlist and function-evaluation/side-effect flags disabled.";
            return true;
        }

        private static bool EvaluateRegisters(IDebugExpressionContext2 context, string[] names, NativeRegistersOutput output, int maximum)
        {
            const enum_DEBUGPROP_INFO_FLAGS fields = enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_NAME |
                enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_TYPE | enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_VALUE;
            var evalFlags = enum_EVALFLAGS.EVAL_NOFUNCEVAL | enum_EVALFLAGS.EVAL_NOSIDEEFFECTS;
            foreach (var name in names)
            {
                if (output.Registers.Count >= maximum) break;
                IDebugExpression2 expression;
                string parseError;
                uint errorIndex;
                var parseHr = context.ParseText("@" + name.ToLowerInvariant(), enum_PARSEFLAGS.PARSE_EXPRESSION, 16, out expression, out parseError, out errorIndex);
                if (ErrorHandler.Failed(parseHr) || expression == null) continue;
                IDebugProperty2 property;
                var evalHr = expression.EvaluateSync(evalFlags, 1000, null, out property);
                if (ErrorHandler.Failed(evalHr) || property == null) continue;
                var info = new DEBUG_PROPERTY_INFO[1];
                var infoHr = property.GetPropertyInfo(fields, 16, 1000, null, 0, info);
                if (ErrorHandler.Failed(infoHr)) continue;
                output.Registers.Add(new NativeRegisterInfo
                {
                    Name = string.IsNullOrWhiteSpace(info[0].bstrName) ? name : info[0].bstrName,
                    Type = info[0].bstrType, Value = info[0].bstrValue,
                    Attributes = "read-only native register expression",
                });
            }
            return output.Registers.Count > 0;
        }

        private (IDebugProgram2 Program, EnvDTE.Process Process, int ThreadId, string ContextId) Paused(int processId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = vs.Dte.Debugger;
            if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode || debugger.CurrentProcess == null || debugger.CurrentProgram == null || debugger.CurrentThread == null)
                throw Error(ToolErrorCode.ExecutionError, "Native inspection requires a paused debug session with a current process and thread.");
            if (processId <= 0 || debugger.CurrentProcess.ProcessID != processId)
                throw Error(ToolErrorCode.InvalidArgument, "processId must identify the currently selected paused debug process.");
            if (vs.DebugEvents == null) throw Error(ToolErrorCode.NotSupported, "The Visual Studio debug-event observer is unavailable.");
            var program = vs.DebugEvents.GetProgram(processId, debugger.CurrentProcess.Name, debugger.CurrentProgram?.Name);
            return (program, debugger.CurrentProcess, debugger.CurrentThread.ID, vs.Debug().BreakContext.Id);
        }

        private static IDebugStackFrame2 Frame(IDebugProgram2 program, uint threadId, int frameIndex)
        {
            IEnumDebugThreads2 threads;
            ErrorHandler.ThrowOnFailure(program.EnumThreads(out threads));
            IDebugThread2 selectedThread = null;
            var oneThread = new IDebugThread2[1];
            while (true)
            {
                uint fetched = 0;
                var hr = threads.Next(1, oneThread, ref fetched);
                ErrorHandler.ThrowOnFailure(hr);
                if (fetched == 0) break;
                uint candidate;
                if (ErrorHandler.Succeeded(oneThread[0].GetThreadId(out candidate)) && candidate == threadId)
                {
                    selectedThread = oneThread[0];
                    break;
                }
            }
            if (selectedThread == null) throw new COMException("The selected DTE thread was not present in the exact observed debug program.", VSConstants.E_FAIL);
            IEnumDebugFrameInfo2 frames;
            ErrorHandler.ThrowOnFailure(selectedThread.EnumFrameInfo(enum_FRAMEINFO_FLAGS.FIF_FRAME, 0, out frames));
            var frameInfo = new FRAMEINFO[1];
            for (var index = 1; ; index++)
            {
                uint fetched = 0;
                var hr = frames.Next(1, frameInfo, ref fetched);
                ErrorHandler.ThrowOnFailure(hr);
                if (fetched == 0) break;
                if (index == frameIndex)
                {
                    if (frameInfo[0].m_pFrame == null) throw new COMException("The engine returned a stack frame without IDebugStackFrame2.", VSConstants.E_NOINTERFACE);
                    return frameInfo[0].m_pFrame;
                }
            }
            throw new COMException("The selected frame index is outside the current stack.", VSConstants.E_INVALIDARG);
        }

        private IDebugProperty2 FindLocal(IDebugStackFrame2 frame, string wanted)
        {
            IEnumDebugPropertyInfo2 properties;
            uint count = 0;
            var filter = LocalsFilter;
            const enum_DEBUGPROP_INFO_FLAGS fields = enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_NAME |
                enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_PROP | enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_NOFUNCEVAL;
            int hr;
            try { hr = frame.EnumProperties(fields, 16, ref filter, 1000, out count, out properties); }
            catch (COMException) { hr = VSConstants.E_NOTIMPL; properties = null; }
            if (ErrorHandler.Failed(hr) || properties == null)
            {
                IDebugProperty2 frameProperties;
                try { hr = frame.GetDebugProperty(out frameProperties); }
                catch (COMException) { hr = VSConstants.E_NOTIMPL; frameProperties = null; }
                if (ErrorHandler.Failed(hr) || frameProperties == null)
                    throw Error(ToolErrorCode.NotSupported, "This engine cannot expose frame properties for memory-context lookup (HRESULT 0x" + hr.ToString("X8") + ").");
                filter = LocalsFilter;
                try { hr = frameProperties.EnumChildren(fields, 16, ref filter, enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_NONE, null, 1000, out properties); }
                catch (COMException) { hr = VSConstants.E_NOTIMPL; properties = null; }
                if (ErrorHandler.Failed(hr) || properties == null)
                    throw Error(ToolErrorCode.NotSupported, "This engine cannot enumerate local child properties (HRESULT 0x" + hr.ToString("X8") + ").");
                hr = properties.GetCount(out count);
                if (ErrorHandler.Failed(hr)) throw Error(ToolErrorCode.NotSupported, "This engine could not report local property count (HRESULT 0x" + hr.ToString("X8") + ").");
            }
            var buffer = new DEBUG_PROPERTY_INFO[64];
            IDebugProperty2 match = null;
            for (uint offset = 0; offset < count; offset += (uint)buffer.Length)
            {
                uint fetched = 0;
                hr = properties.Next((uint)Math.Min(buffer.Length, (int)(count - offset)), buffer, out fetched);
                if (ErrorHandler.Failed(hr) && fetched == 0) break;
                for (var i = 0; i < fetched; i++)
                {
                    if (!string.Equals(buffer[i].bstrName, wanted, StringComparison.Ordinal)) continue;
                    if (match != null) throw Error(ToolErrorCode.InvalidArgument, "The frame contains more than one local with the requested exact name.");
                    match = buffer[i].pProperty;
                }
                if (fetched == 0) break;
            }
            return match ?? throw Error(ToolErrorCode.InvalidArgument, "No exact local with that name exists in the selected frame.");
        }

        private IDebugProperty2 EvaluateLocalAddress(IDebugStackFrame2 frame, string variableName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(variableName) || !variableName.All(c => char.IsLetterOrDigit(c) || c == '_') || char.IsDigit(variableName[0]))
                throw Error(ToolErrorCode.InvalidArgument, "variableName must be a simple C/C++ identifier when the engine requires safe expression-context lookup.");
            IDebugExpressionContext2 context;
            var contextHr = frame.GetExpressionContext(out context);
            if (ErrorHandler.Failed(contextHr) || context == null)
                throw Error(ToolErrorCode.NotSupported, "The engine does not expose an expression context for this frame (HRESULT 0x" + contextHr.ToString("X8") + ").");
            IDebugExpression2 expression;
            string parseError;
            uint errorIndex;
            var parseHr = context.ParseText("&" + variableName, enum_PARSEFLAGS.PARSE_EXPRESSION, 16, out expression, out parseError, out errorIndex);
            if (ErrorHandler.Failed(parseHr) || expression == null)
                throw Error(ToolErrorCode.NotSupported, "The engine could not parse the address of this exact local without function evaluation: " + parseError);
            IDebugProperty2 property;
            var evalFlags = enum_EVALFLAGS.EVAL_NOFUNCEVAL | enum_EVALFLAGS.EVAL_NOSIDEEFFECTS;
            var evalHr = expression.EvaluateSync(evalFlags, 1000, null, out property);
            if (ErrorHandler.Failed(evalHr) || property == null)
                throw Error(ToolErrorCode.NotSupported, "The engine could not evaluate this direct local address with function calls and side effects disabled (HRESULT 0x" + evalHr.ToString("X8") + ").");
            return property;
        }

        private IDebugMemoryContext2 MemoryContextAtAddress(IDebugStackFrame2 frame, string address)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!TryHexAddress(address, out var targetAddress)) throw Error(ToolErrorCode.InvalidArgument, "The debugger returned an address that is not hexadecimal.");
            IDebugCodeContext2 code;
            var codeHr = frame.GetCodeContext(out code);
            if (ErrorHandler.Failed(codeHr)) throw Error(ToolErrorCode.NotSupported, "The selected frame has no code context for address-relative memory reads (HRESULT 0x" + codeHr.ToString("X8") + ").");
            if (code == null) throw Error(ToolErrorCode.NotSupported, "The selected frame does not expose a code context for address-relative memory access.");
            var info = new CONTEXT_INFO[1];
            var infoHr = code.GetInfo(enum_CONTEXT_INFO_FIELDS.CIF_ADDRESSABSOLUTE, info);
            if (ErrorHandler.Failed(infoHr) || !TryHexAddress(info[0].bstrAddressAbsolute, out var frameAddress))
                throw Error(ToolErrorCode.NotSupported, "The engine did not expose a parseable absolute address for the current frame code context.");
            var delta = targetAddress >= frameAddress ? targetAddress - frameAddress : frameAddress - targetAddress;
            IDebugMemoryContext2 memoryContext;
            var addressHr = targetAddress >= frameAddress ? code.Add(delta, out memoryContext) : code.Subtract(delta, out memoryContext);
            if (ErrorHandler.Failed(addressHr) || memoryContext == null)
                throw Error(ToolErrorCode.NotSupported, "The engine could not derive a memory context for the requested address from the current frame code context.");
            return memoryContext;
        }

        private void ValidateFrame(int frameIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (frameIndex < 1 || frameIndex > 256) throw Error(ToolErrorCode.InvalidArgument, "frameIndex must be between 1 and 256.");
        }

        private static string Normalize(string path) => string.IsNullOrWhiteSpace(path) ? string.Empty : System.IO.Path.GetFullPath(path);
        private static bool TryHexAddress(string value, out ulong address)
        {
            address = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value.Substring(2);
            return ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
        }
        private McpToolException Error(ToolErrorCode code, string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new McpToolException(code, message, new Newtonsoft.Json.Linq.JObject { ["instance"] = vs.Instance() });
        }
    }
}
