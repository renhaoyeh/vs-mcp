#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VsMcp.Contracts
{
    internal sealed class SourceLocation
    {
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public int Line { get; set; }
        [JsonRequired] public int Column { get; set; }
        [JsonRequired] public int EndLine { get; set; }
        [JsonRequired] public int EndColumn { get; set; }
    }

    internal sealed class NavigationResult
    {
        [JsonRequired] public string Symbol { get; set; } = "";
        [JsonRequired] public string Kind { get; set; } = "";
        [JsonRequired] public string Language { get; set; } = "";
        [JsonRequired] public List<SourceLocation> Locations { get; set; } = new List<SourceLocation>();
        [JsonRequired] public bool Truncated { get; set; }
        // A referenced framework symbol can have metadata definitions but no source file.
        [JsonRequired] public bool HasMetadataDefinition { get; set; }
    }
}
