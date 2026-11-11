using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using ModelContextProtocol.Server;
using VsMcp.Contracts;
using VsMcp.Services.Debugging;
using VsMcp.Services.VisualStudio;

namespace VsMcp.Tools
{
    internal sealed class NativeInspectionTools
    {
        private readonly VsContext vs;
        private readonly NativeInspectionService inspection;

        public NativeInspectionTools(VsContext context)
        {
            vs = context;
            inspection = new NativeInspectionService(context);
        }

        [McpServerTool(Name = "debugger_list_native_transports", Title = "List debugger transports and engines", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Enumerate transports and the debugger engines installed for each transport through Debugger2 in the connected Visual Studio instance. Includes local/default transports when VS exposes them. Does not attach, launch or alter debugger settings.")]
        public async Task<NativeDebuggerInventory> InventoryAsync(CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return inspection.Inventory();
        }

        [McpServerTool(Name = "debugger_load_module_symbols", Title = "Load symbols for a paused module", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
        [Description("Request PDB loading for exactly one module in the currently selected paused debug process through Process3.Modules and EnvDTE90.Module.LoadSymbols. processId must equal the current process; moduleName is exact and modulePath can disambiguate duplicate names. pdbPath is optional and, when supplied, must be an existing absolute PDB path. Reads back SymbolFile. Visual Studio can ignore pdbPath when symbols are already visible on its configured search path; a load request or readback does not prove that the requested PDB was used or matches the module. Suppresses symbol-load UI for this call, restores the prior setting, and never resumes the target.")]
        public async Task<NativeSymbolsOutput> LoadSymbolsAsync(
            [Description("PID of the currently selected paused debug process.")] int processId,
            [Description("Exact module name from the current Process3.Modules collection.")] string moduleName,
            [Description("Optional exact absolute loaded-module path, used to disambiguate duplicate names.")] string modulePath = null,
            [Description("Optional exact absolute PDB path. Visual Studio may ignore it if symbols are already visible on its configured search path.")] string pdbPath = null,
            CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return inspection.LoadSymbols(processId, moduleName, modulePath, pdbPath);
        }

        [McpServerTool(Name = "debugger_read_native_memory", Title = "Read paused native memory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Read a bounded number of bytes either from an exact local variable's IDebugProperty2.GetMemoryContext or from a hexadecimal address derived from the selected frame's code context; bytes are read by the observed process's IDebugProgram2.GetMemoryBytes. When property enumeration is unavailable, variableName is restricted to one identifier and parsed as an address-of expression with EVAL_NOFUNCEVAL and EVAL_NOSIDEEFFECTS. Supply exactly one of variableName/address. Reports partial bytes for inaccessible/unmapped ranges. Never uses OS process-memory APIs, writes memory or resumes execution.")]
        public async Task<NativeMemoryOutput> ReadMemoryAsync(
            [Description("PID of the currently selected paused debug process.")] int processId,
            [Description("Optional exact local variable name in the selected frame; the engine must expose its memory context. Supply exactly one of variableName/address.")] string variableName = null,
            [Description("Optional hexadecimal target address, such as 0x7FF600001000; derived relative to the selected frame's engine code context. Supply exactly one of variableName/address.")] string address = null,
            [Range(1, 4096), Description("Maximum bytes to request from the debugger engine.")] int byteCount = 16,
            [Range(1, 256), Description("One-based frame index in the current thread; defaults to the top frame.")] int frameIndex = 1,
            CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return inspection.ReadMemory(processId, variableName, address, byteCount, frameIndex);
        }

        [McpServerTool(Name = "debugger_disassemble", Title = "Disassemble the paused native frame", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Read a bounded instruction sequence from the selected paused thread/frame via the exact observed IDebugProgram2 disassembly stream and stack-frame code context. Requires the current processId. Does not change the instruction pointer, step or resume. Engines that do not provide a stream fail explicitly.")]
        public async Task<NativeDisassemblyOutput> DisassembleAsync(
            [Description("PID of the currently selected paused debug process.")] int processId,
            [Range(1, 256), Description("One-based frame index in the current thread; defaults to the top frame.")] int frameIndex = 1,
            [Range(1, 256), Description("Maximum instructions to return.")] int maxInstructions = 32,
            CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return inspection.Disassemble(processId, frameIndex, maxInstructions);
        }

        [McpServerTool(Name = "debugger_read_registers", Title = "Read paused native registers", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Read bounded CPU registers for the selected paused stack frame through IDebugStackFrame2.EnumProperties with the standard register filter. If the engine omits that filter, use the selected frame's expression context with a fixed architecture register allowlist and EVAL_NOFUNCEVAL/EVAL_NOSIDEEFFECTS. No caller expression is accepted; returns Supported=false when the engine exposes neither path. Does not write registers or resume execution.")]
        public async Task<NativeRegistersOutput> RegistersAsync(
            [Description("PID of the currently selected paused debug process.")] int processId,
            [Range(1, 256), Description("One-based frame index in the current thread; defaults to the top frame.")] int frameIndex = 1,
            [Range(1, 256), Description("Maximum register properties to return.")] int maxRegisters = 128,
            CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return inspection.Registers(processId, frameIndex, maxRegisters);
        }
    }
}
