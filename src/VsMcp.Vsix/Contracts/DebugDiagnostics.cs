#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace VsMcp.Contracts
{
    internal enum DebugDumpKind { Normal, Full }
    internal class DebugSymbolSettings
    {
        public string SymbolPath { get; set; } = "";
        public string SymbolPathState { get; set; } = "";
        public string SymbolCachePath { get; set; } = "";
        public bool OnlyLoadSymbolsManually { get; set; }
    }
    internal sealed class DebugSymbolSettingsOutput : DebugSymbolSettings
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        public DebugSymbolSettings? Previous { get; set; }
        [Description("Whether the call requested immediate symbol loading. Use module symbol status to inspect the result.")]
        public bool LoadSymbolsRequested { get; set; }
    }
    internal sealed class DebugDumpSaved
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public DebugDumpKind Kind { get; set; }
        [JsonRequired] public int ProcessId { get; set; }
        [JsonRequired] public long Length { get; set; }
        [JsonRequired] public DateTime LastWriteTimeUtc { get; set; }
    }
    internal sealed class DebugModuleInfo
    {
        public Guid? ProgramId { get; set; }
        public string? Name { get; set; }
        public string? Path { get; set; }
        [Description("Version reported by the debug engine, not a potentially newer local DLL.")]
        public string? Version { get; set; }
        public string? SymbolPath { get; set; }
        public string? SymbolStatus { get; set; }
        public string? LoadAddress { get; set; }
        public uint? Size { get; set; }
        [Description("Timestamp reported by the debug engine. Not proof of source/build identity.")]
        public DateTime? TimestampUtc { get; set; }
        public string? Flags { get; set; }
        public string? Error { get; set; }
    }
    internal sealed class DebugModulesOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public int ProcessId { get; set; }
        [JsonRequired] public List<DebugModuleInfo> Items { get; set; } = new List<DebugModuleInfo>();
        [JsonRequired] public bool Truncated { get; set; }
    }
    internal sealed class DebugObservedException
    {
        public int? ProcessId { get; set; }
        public Guid ProgramId { get; set; }
        public Guid? ProcessGuid { get; set; }
        public uint? ThreadId { get; set; }
        public string? Name { get; set; }
        public string? Code { get; set; }
        public string? Description { get; set; }
        public string? State { get; set; }
        public Guid Category { get; set; }
        public DateTime ObservedAtUtc { get; set; }
    }
    internal sealed class DebugExceptionOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public int ProcessId { get; set; }
        public uint? CurrentThreadId { get; set; }
        [JsonRequired] public string BreakReason { get; set; } = "";
        [Description("Last engine exception event observed for this paused process. Null when none was observed; not proof of a crash or of the cause of the current stop.")]
        public DebugObservedException? LastObservedException { get; set; }
        public string? ObservationError { get; set; }
    }
}
