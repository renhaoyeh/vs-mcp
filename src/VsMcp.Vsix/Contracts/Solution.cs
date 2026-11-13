#nullable enable
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

// Structured results of the solution query tools. [JsonRequired] marks a required property and
// T? a value that may be null.
namespace VsMcp.Contracts
{
    [Description("The Visual Studio instance that served the call.")]
    internal sealed class InstanceInfo
    {
        [JsonRequired] public int Pid { get; set; }
        [JsonRequired] public string Product { get; set; } = "";
        [JsonRequired] public string DteVersion { get; set; } = "";
        public string? Version { get; set; }
        public string? ExecutablePath { get; set; }
        [JsonRequired] public bool SolutionOpen { get; set; }

        [Description("Null for an unsaved solution.")]
        public string? SolutionPath { get; set; }
    }

    internal sealed class ProjectName
    {
        [JsonRequired] public string Name { get; set; } = "";
        [JsonRequired] public string UniqueName { get; set; } = "";
    }

    internal sealed class ProjectInfo
    {
        [Description("True when loaded, false when unloaded in this solution (use project_reload before building), null when the solution service is unavailable.")]
        public bool? IsLoaded { get; set; }
        [JsonRequired] public string Name { get; set; } = "";
        [JsonRequired] public string UniqueName { get; set; } = "";
        public string? FullName { get; set; }
        [JsonRequired] public string Kind { get; set; } = "";
    }

    internal sealed class FolderInfo
    {
        [JsonRequired] public string Name { get; set; } = "";

        [JsonRequired]
        [Description("The '/'-separated path to pass as 'folder'.")]
        public string Path { get; set; } = "";

        [JsonRequired] public int FolderCount { get; set; }
        [JsonRequired] public int ProjectCount { get; set; }

        [JsonRequired]
        [Description("Projects in this folder and every folder below it.")]
        public int TotalProjectCount { get; set; }

        [Description("Present only when 'recursive' was set.")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<FolderInfo>? Folders { get; set; }

        [Description("Present only when 'recursive' was set.")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ProjectInfo>? Projects { get; set; }
    }

    internal sealed class GetProjectsOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();

        [Description("The folder that was listed, or null for the top level.")]
        [JsonRequired]
        public string? Folder { get; set; }

        [JsonRequired] public List<FolderInfo> Folders { get; set; } = new List<FolderInfo>();
        [JsonRequired] public List<ProjectInfo> Projects { get; set; } = new List<ProjectInfo>();
    }

    internal sealed class ProjectDependencies
    {
        [JsonRequired] public string Name { get; set; } = "";
        [JsonRequired] public string UniqueName { get; set; } = "";

        [Description("The projects this project directly requires.")]
        [JsonRequired]
        public List<ProjectName> DirectDependencies { get; set; } = new List<ProjectName>();

        [Description("Every project that must be built before this one, direct and indirect.")]
        [JsonRequired]
        public List<ProjectName> AllDependencies { get; set; } = new List<ProjectName>();
    }

    internal sealed class GetProjectDependenciesOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public List<ProjectDependencies> Projects { get; set; } = new List<ProjectDependencies>();
    }

    internal sealed class ActiveConfiguration
    {
        public string SolutionConfiguration { get; set; } = "";
        public string SolutionPlatform { get; set; } = "";
    }

    internal sealed class ProjectMapping
    {
        [JsonRequired] public string Project { get; set; } = "";
        [JsonRequired] public string UniqueName { get; set; } = "";
        [JsonRequired] public string ProjectConfiguration { get; set; } = "";
        [JsonRequired] public string ProjectPlatform { get; set; } = "";

        [Description("False when this solution configuration does not build the project.")]
        [JsonRequired]
        public bool ShouldBuild { get; set; }

        [JsonRequired] public bool ShouldDeploy { get; set; }
    }

    internal sealed class SolutionConfigurationInfo
    {
        [JsonRequired] public string SolutionConfiguration { get; set; } = "";
        [JsonRequired] public string SolutionPlatform { get; set; } = "";
        [JsonRequired] public bool IsActive { get; set; }

        [Description("Each project and the project configuration and platform it maps to in this pair.")]
        [JsonRequired]
        public List<ProjectMapping> Projects { get; set; } = new List<ProjectMapping>();
    }

    internal sealed class GetSolutionConfigurationsOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        public ActiveConfiguration? ActiveConfiguration { get; set; }
        [JsonRequired] public List<SolutionConfigurationInfo> SolutionConfigurations { get; set; } = new List<SolutionConfigurationInfo>();
    }
}
