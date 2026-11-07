using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace VsMcp.Mcp
{
    /// <summary>Expected failures reported by the Visual Studio tools.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    internal enum ToolErrorCode
    {
        [EnumMember(Value = "NOT_SUPPORTED")]
        NotSupported,
        [EnumMember(Value = "AMBIGUOUS_PROJECT")]
        AmbiguousProject,
        [EnumMember(Value = "BUILD_IN_PROGRESS")]
        BuildInProgress,
        [EnumMember(Value = "BUILD_NOT_STARTED")]
        BuildNotStarted,
        [EnumMember(Value = "COMPILE_ERROR")]
        CompileError,
        [EnumMember(Value = "CONFIGURATION_NOT_FOUND")]
        ConfigurationNotFound,
        [EnumMember(Value = "DEBUGGER_ACTIVE")]
        DebuggerActive,
        [EnumMember(Value = "DEBUG_LAUNCH_PENDING")]
        DebugLaunchPending,
        [EnumMember(Value = "DEBUG_OPERATION_PENDING")]
        DebugOperationPending,
        [EnumMember(Value = "DEBUG_START_FAILED")]
        DebugStartFailed,
        [EnumMember(Value = "DEBUG_START_UNAVAILABLE")]
        DebugStartUnavailable,
        [EnumMember(Value = "DEBUG_STOP_UNAVAILABLE")]
        DebugStopUnavailable,
        [EnumMember(Value = "DUPLICATE_PROJECT")]
        DuplicateProject,
        [EnumMember(Value = "EXECUTION_ERROR")]
        ExecutionError,
        [EnumMember(Value = "FOLDER_NOT_FOUND")]
        FolderNotFound,
        [EnumMember(Value = "INVALID_ARGUMENT")]
        InvalidArgument,
        [EnumMember(Value = "NO_ACTIVE_CONFIGURATION")]
        NoActiveConfiguration,
        [EnumMember(Value = "NO_BUILD")]
        NoBuild,
        [EnumMember(Value = "NO_SOLUTION")]
        NoSolution,
        [EnumMember(Value = "NO_STARTUP_PROJECT")]
        NoStartupProject,
        [EnumMember(Value = "PROJECT_NOT_BUILT_IN_CONFIGURATION")]
        ProjectNotBuiltInConfiguration,
        [EnumMember(Value = "PROJECT_NOT_FOUND")]
        ProjectNotFound,
        [EnumMember(Value = "SERIALIZATION_ERROR")]
        SerializationError,
    }
}
