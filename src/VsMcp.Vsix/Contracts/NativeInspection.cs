#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace VsMcp.Contracts
{
    internal sealed class NativeDebuggerInventory
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public List<NativeTransportInfo> Transports { get; set; } = new List<NativeTransportInfo>();
    }

    internal sealed class NativeTransportInfo
    {
        public string? Name { get; set; }
        public string? Id { get; set; }
        public List<NativeEngineInfo> Engines { get; set; } = new List<NativeEngineInfo>();
    }

    internal sealed class NativeEngineInfo
    {
        public string? Name { get; set; }
        public string? Id { get; set; }
        public string? AttachResult { get; set; }
    }

    internal sealed class NativeSymbolsOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public string ContextId { get; set; } = "";
        [JsonRequired] public int ProcessId { get; set; }
        [JsonRequired] public string ModuleName { get; set; } = "";
        [JsonRequired] public string ModulePath { get; set; } = "";
        public string? RequestedPdbPath { get; set; }
        public string? ActualSymbolFile { get; set; }
        public bool LoadRequested { get; set; }
        [Description("A load request does not prove that the requested PDB was used or matches the loaded module. VS can keep symbols already found on its configured search path and ignore RequestedPdbPath.")]
        public string Note { get; set; } = "";
    }

    internal sealed class NativeMemoryOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public string ContextId { get; set; } = "";
        [JsonRequired] public int ProcessId { get; set; }
        [JsonRequired] public int ThreadId { get; set; }
        [JsonRequired] public int FrameIndex { get; set; }
        public string? VariableName { get; set; }
        public string? Address { get; set; }
        [JsonRequired] public int RequestedBytes { get; set; }
        [JsonRequired] public int BytesRead { get; set; }
        [JsonRequired] public string HexBytes { get; set; } = "";
        [JsonRequired] public bool Partial { get; set; }
        public uint? UnreadableBytes { get; set; }
        public string? Error { get; set; }
        [Description("Reads only from the paused debug engine's memory interface. Results can be partial at inaccessible or unmapped ranges; no OS process-memory API is used.")]
        public string Backend { get; set; } = "Visual Studio debug engine IDebugProgram2.GetMemoryBytes via IDebugProperty2.GetMemoryContext or the selected frame code context.";
    }

    internal sealed class NativeInstruction
    {
        public string? Address { get; set; }
        public string? Bytes { get; set; }
        public string? Opcode { get; set; }
        public string? Operands { get; set; }
        public string? Symbol { get; set; }
    }

    internal sealed class NativeDisassemblyOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public string ContextId { get; set; } = "";
        [JsonRequired] public int ProcessId { get; set; }
        [JsonRequired] public int ThreadId { get; set; }
        [JsonRequired] public int FrameIndex { get; set; }
        [JsonRequired] public int RequestedInstructions { get; set; }
        [JsonRequired] public List<NativeInstruction> Instructions { get; set; } = new List<NativeInstruction>();
        [JsonRequired] public bool Truncated { get; set; }
        [Description("Disassembly is supplied by the current Visual Studio debug engine and may be unavailable for some targets or frames.")]
        public string Backend { get; set; } = "Visual Studio debug engine IDebugProgram2.GetDisassemblyStream from the selected stack frame code context.";
    }

    internal sealed class NativeRegisterInfo
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? Value { get; set; }
        public string? Attributes { get; set; }
    }

    internal sealed class NativeRegistersOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public string ContextId { get; set; } = "";
        [JsonRequired] public int ProcessId { get; set; }
        [JsonRequired] public int ThreadId { get; set; }
        [JsonRequired] public int FrameIndex { get; set; }
        [JsonRequired] public List<NativeRegisterInfo> Registers { get; set; } = new List<NativeRegisterInfo>();
        [JsonRequired] public bool Supported { get; set; }
        [JsonRequired] public bool Truncated { get; set; }
        public string? UnsupportedReason { get; set; }
        [Description("Registers are read through the engine's stack-frame register filter or selected-frame expression context with calls and side effects disabled; no register is changed.")]
        public string Backend { get; set; } = "IDebugStackFrame2.EnumProperties with the standard register filter, or the selected frame's fixed native register expression allowlist.";
    }
}
