#nullable enable
using System.Text.Json.Serialization;

namespace VsMcp.Contracts
{
    internal sealed class ToolResponse<T>
    {
        [JsonRequired] public InstanceInfo Instance { get; set; } = new InstanceInfo();
        [JsonRequired] public T? Data { get; set; }
    }
}
