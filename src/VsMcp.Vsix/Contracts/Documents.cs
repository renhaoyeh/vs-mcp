#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;
namespace VsMcp.Contracts
{
    internal sealed class DocumentState
    {
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public bool Saved { get; set; }
        [JsonRequired] public bool ReadOnly { get; set; }
        public string? Sha256 { get; set; }
    }
    internal sealed class DocumentList { [JsonRequired] public List<DocumentState> Documents { get; set; } = new List<DocumentState>(); }
    internal sealed class DocumentContent
    {
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public string Text { get; set; } = "";
        [JsonRequired] public string Sha256 { get; set; } = "";
        [JsonRequired] public int StartLine { get; set; }
        [JsonRequired] public int TotalLines { get; set; }
        [JsonRequired] public bool Truncated { get; set; }
    }
    internal sealed class DocumentEditResult
    {
        [JsonRequired] public DocumentState Document { get; set; } = new DocumentState();
        [JsonRequired] public int Replacements { get; set; }
    }
    internal sealed class EditorSelection
    {
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public int Line { get; set; }
        [JsonRequired] public int Column { get; set; }
        [JsonRequired] public int SelectionStartLine { get; set; }
        [JsonRequired] public int SelectionStartColumn { get; set; }
        [JsonRequired] public int SelectionEndLine { get; set; }
        [JsonRequired] public int SelectionEndColumn { get; set; }
        [JsonRequired] public string SelectedText { get; set; } = "";
        [JsonRequired] public bool Truncated { get; set; }
        [JsonRequired] public bool BlockSelection { get; set; }
    }
}
