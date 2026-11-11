#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace VsMcp.Contracts
{
    internal sealed class DebuggerProcessInfo
    {
        public int ProcessId { get; set; }
        public string Name { get; set; } = "";
        public string? Path { get; set; }
        public string? PathError { get; set; }
        public bool IsDebugged { get; set; }
        public bool IsCurrent { get; set; }
    }

    internal sealed class DebuggerProcessListOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        public string? ContextId { get; set; }
        public int? CurrentProcessId { get; set; }
        public List<DebuggerProcessInfo> LocalProcesses { get; set; } = new List<DebuggerProcessInfo>();
        public List<DebuggerProcessInfo> DebuggedProcesses { get; set; } = new List<DebuggerProcessInfo>();
        public string Mode { get; set; } = "";
        public bool Truncated { get; set; }
    }

    internal sealed class DebuggerWorkflowResult<T>
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public T Data { get; set; } = default!;
    }

    internal sealed class DebuggerBreakpointInfo
    {
        public List<string> ReadErrors { get; set; } = new List<string>();
        public string? Handle { get; set; }
        public string LocationType { get; set; } = "";
        public string? File { get; set; }
        public int? Line { get; set; }
        public int? Column { get; set; }
        public string? FunctionName { get; set; }
        public string? Language { get; set; }
        public string? Condition { get; set; }
        public string ConditionType { get; set; } = "";
        public int? BoundChildrenCount { get; set; }
        public int HitCountTarget { get; set; }
        public string HitCountType { get; set; } = "";
        public int CurrentHits { get; set; }
        public bool Enabled { get; set; }
        public string Binding { get; set; } = "unknown";
        public string? BoundAddressOrError { get; set; }
    }

    internal sealed class DebuggerBreakpointListOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        public List<DebuggerBreakpointInfo> Items { get; set; } = new List<DebuggerBreakpointInfo>();
        public bool Truncated { get; set; }
    }

    internal sealed class DebuggerVariableNode
    {
        public string Name { get; set; } = "";
        public string? Type { get; set; }
        public string? Value { get; set; }
        public bool IsValidValue { get; set; }
        [Description("Whether the debugger reports child data members. Reading children or values may evaluate debuggee expressions.")]
        public bool HasChildren { get; set; }
        public List<DebuggerVariableNode> Children { get; set; } = new List<DebuggerVariableNode>();
    }

    internal sealed class DebuggerVariablesOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        public string ContextId { get; set; } = "";
        public int ProcessId { get; set; }
        public int ThreadId { get; set; }
        public int FrameIndex { get; set; }
        public List<DebuggerVariableNode> Items { get; set; } = new List<DebuggerVariableNode>();
        public bool Truncated { get; set; }
        public bool EvaluationMayExecute { get; set; } = true;
    }

    internal sealed class DebuggerExceptionInfo
    {
        public string Group { get; set; } = "";
        public string Name { get; set; } = "";
        public uint? Code { get; set; }
        public bool BreakWhenThrown { get; set; }
        public bool BreakWhenUserUnhandled { get; set; }
    }

    internal sealed class DebuggerExceptionGroupInfo
    {
        public string Name { get; set; } = "";
        public bool SupportsExceptionCodes { get; set; }
        public int SettingCount { get; set; }
    }

    internal sealed class DebuggerExceptionSettingInfo
    {
        public string Name { get; set; } = "";
        public uint? Code { get; set; }
        public bool BreakWhenThrown { get; set; }
        public bool BreakWhenUserUnhandled { get; set; }
    }
}
