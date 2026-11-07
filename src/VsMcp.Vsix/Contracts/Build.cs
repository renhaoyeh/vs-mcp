#nullable enable
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

// Structured results of the build tools. [JsonRequired] marks a required property and
// T? a value that may be null.
namespace VsMcp.Contracts
{
    internal enum BuildOperation
    {
        Build,
        Clean,
        Rebuild,
    }

    internal enum BuildStatus
    {
        Succeeded,
        Failed,
        Cancelled,
        Running,
    }

    internal enum ProjectBuildResult
    {
        Succeeded,
        Failed,
        Cancelled,
        NotBuilt,
        Running,
    }

    internal sealed class BuildProjectOutput
    {
        [JsonRequired] public string Project { get; set; } = "";
        [JsonRequired] public string UniqueName { get; set; } = "";
        [JsonRequired] public string ProjectConfiguration { get; set; } = "";
        [JsonRequired] public string ProjectPlatform { get; set; } = "";
        [JsonRequired] public ProjectBuildResult Result { get; set; }
    }

    internal sealed class BuildError
    {
        public string? Project { get; set; }
        public string? File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string? Description { get; set; }
    }

    internal sealed class SkippedProject
    {
        public string Project { get; set; } = "";
        public string UniqueName { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    internal sealed class BuildOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public string SolutionConfiguration { get; set; } = "";
        [JsonRequired] public string SolutionPlatform { get; set; } = "";

        [Description("What was done to the projects: build, clean or rebuild.")]
        [JsonRequired]
        public BuildOperation Operation { get; set; }

        [Description("'running' while Visual Studio is still building; otherwise how the build ended.")]
        [JsonRequired]
        public BuildStatus Status { get; set; }

        [JsonRequired] public bool Finished { get; set; }
        [JsonRequired] public bool Running { get; set; }

        [Description("One entry per requested project, in build order.")]
        [JsonRequired]
        public List<BuildProjectOutput> Projects { get; set; } = new List<BuildProjectOutput>();

        public int ErrorCount { get; set; }
        public List<BuildError> Errors { get; set; } = new List<BuildError>();

        [Description("True when the build was cancelled because a project failed (stopOnFirstError).")]
        public bool StoppedEarly { get; set; }

        [Description("Projects of a folder build that the configuration does not build.")]
        public List<SkippedProject> Skipped { get; set; } = new List<SkippedProject>();

        [Description("The tail of the Build output pane; null while running or when it cannot be read.")]
        public string? Output { get; set; }

        public bool OutputTruncated { get; set; }
    }

    internal sealed class CancelBuildOutput
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public bool CancelRequested { get; set; }
        [JsonRequired] public bool Running { get; set; }
    }
}
