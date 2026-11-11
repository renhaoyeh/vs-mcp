#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VsMcp.Contracts
{
    internal sealed class ProjectWorkflowOutput<T>
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public T Data { get; set; } = default!;
    }

    internal sealed class CppPropertyValue
    {
        public string? Raw { get; set; }
        public string? Evaluated { get; set; }
        public string Source { get; set; } = "unavailable";
    }

    internal sealed class CppConfigurationOutput
    {
        public string Project { get; set; } = "";
        public string? File { get; set; }
        public string Configuration { get; set; } = "";
        public string Platform { get; set; } = "";
        public Dictionary<string, CppPropertyValue> Properties { get; set; } = new Dictionary<string, CppPropertyValue>();
    }

    internal sealed class CppLaunchSettings
    {
        public string? Command { get; set; }
        public string? Arguments { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? Environment { get; set; }
        public bool? MergeEnvironment { get; set; }
        public string DebuggerType { get; set; } = "";
    }

    internal sealed class CppLaunchSettingsOutput
    {
        public string Project { get; set; } = "";
        public string Configuration { get; set; } = "";
        public string Platform { get; set; } = "";
        public CppLaunchSettings? Previous { get; set; }
        public CppLaunchSettings Values { get; set; } = new CppLaunchSettings();
    }

    internal sealed class ProjectOutputsOutput
    {
        public string Project { get; set; } = "";
        public string Configuration { get; set; } = "";
        public string Platform { get; set; } = "";
        public string? PrimaryOutput { get; set; }
        public string? ProgramDatabase { get; set; }
        public string? ImportLibrary { get; set; }
        public string? OutputDirectory { get; set; }
        public string? IntermediateDirectory { get; set; }
        public List<ProjectOutputGroupInfo> OutputGroups { get; set; } = new List<ProjectOutputGroupInfo>();
        public Dictionary<string, string> Unavailable { get; set; } = new Dictionary<string, string>();
    }

    internal sealed class ProjectOutputGroupInfo
    {
        public string CanonicalName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Role { get; set; } = "other";
        public List<string> Paths { get; set; } = new List<string>();
    }

    internal sealed class ProjectItemMutationOutput
    {
        public string Project { get; set; } = "";
        public string Item { get; set; } = "";
        public string? FilePath { get; set; }
        public bool Added { get; set; }
        public bool Removed { get; set; }
        public bool FileStillExists { get; set; }
        public string? Filter { get; set; }
    }
}
