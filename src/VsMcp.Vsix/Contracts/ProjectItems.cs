using System.Collections.Generic;

namespace VsMcp.Contracts
{
    internal sealed class GetProjectItemsOutput
    {
        public InstanceInfo Instance { get; set; }
        public string Project { get; set; }
        public List<ProjectItemInfo> Items { get; set; }
        public bool Truncated { get; set; }
    }

    internal sealed class ProjectItemInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Kind { get; set; }
        public string FilePath { get; set; }
    }
}
