using VsMcp.Services.VisualStudio;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VsMcp.Contracts;
using VsMcp.Mcp;
using VsMcp.Services.Documents;
namespace VsMcp.Tools
{
    internal sealed class DocumentTools
    {
        private readonly VsContext vs;
        private readonly DocumentService documents;
        public DocumentTools(VsContext context, DocumentService service) { vs = context; documents = service; }
        private async Task<ToolResponse<T>> InvokeAsync<T>(Func<T> operation, CancellationToken token)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(token); var instance = vs.ReadInstance();
            try { return new ToolResponse<T> { Instance = instance, Data = operation() }; }
            catch (ArgumentException e) { throw new McpToolException(ToolErrorCode.InvalidArgument, e.Message); }
            catch (InvalidOperationException e) { throw new McpToolException(ToolErrorCode.ExecutionError, e.Message); }
        }
        [McpServerTool(Name = "document_list", Title = "List editor buffers", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("List open Visual Studio documents, including unsaved and read-only state. Works with C++ and C# editors.")]
        public Task<ToolResponse<DocumentList>> ListAsync(CancellationToken cancellationToken = default) => InvokeAsync(documents.List, cancellationToken);
        [McpServerTool(Name = "document_active", Title = "Read active document", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Read the active document state. data is null when no document is active.")]
        public Task<ToolResponse<DocumentState>> ActiveAsync(CancellationToken cancellationToken = default) => InvokeAsync(documents.Active, cancellationToken);
        [McpServerTool(Name = "document_open", Title = "Open an editor buffer", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Open an existing absolute file path in this IDE without creating files or adding project items.")]
        public Task<ToolResponse<DocumentState>> OpenAsync(string path, CancellationToken cancellationToken = default) => InvokeAsync(() => documents.Open(path), cancellationToken);
        [McpServerTool(Name = "document_read", Title = "Read an editor buffer", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Read an open text buffer including unsaved changes. Returns bounded text and a SHA-256 of the complete buffer for subsequent edits. Positions are one-based; omit path for the active document.")]
        public Task<ToolResponse<DocumentContent>> ReadAsync(string path = null, [Range(1, int.MaxValue)] int startLine = 1, [Range(1, 5000)] int maxLines = 500, [Range(1, 200000)] int maxCharacters = 20000, CancellationToken cancellationToken = default) => InvokeAsync(() => documents.Read(path, startLine, maxLines, maxCharacters), cancellationToken);
        [McpServerTool(Name = "document_write", Title = "Write an editor buffer", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true), Description("Replace an entire open text buffer in one undo transaction. Requires complete-buffer expectedSha256 from document_read; stale edits fail without changing text. Leaves edits unsaved unless save is true.")]
        public Task<ToolResponse<DocumentEditResult>> WriteAsync(string text, string expectedSha256, string path = null, bool save = false, CancellationToken cancellationToken = default) => InvokeAsync(() => documents.Write(path, text, expectedSha256, save), cancellationToken);
        [McpServerTool(Name = "editor_replace", Title = "Replace exact buffer text", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true), Description("Replace an exact case-sensitive fragment in an open C++/C# buffer in one undo transaction. Requires expectedSha256 from document_read. Missing, ambiguous or stale matches fail without editing. Set replaceAll explicitly to allow multiple matches. Does not save.")]
        public Task<ToolResponse<DocumentEditResult>> ReplaceAsync(string oldText, string newText, string expectedSha256, string path = null, bool replaceAll = false, CancellationToken cancellationToken = default) => InvokeAsync(() => documents.Replace(path, oldText, newText, expectedSha256, replaceAll), cancellationToken);
        [McpServerTool(Name = "document_save", Title = "Save an editor buffer", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Save an open document to its existing path and verify that saving completed.")]
        public Task<ToolResponse<DocumentState>> SaveAsync(string path = null, CancellationToken cancellationToken = default) => InvokeAsync(() => documents.Save(path), cancellationToken);
        [McpServerTool(Name = "document_close", Title = "Close an editor buffer", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true), Description("Close an open document. Reject unsaved changes unless save or discardChanges is explicitly true.")]
        public Task<ToolResponse<DocumentState>> CloseAsync(string path = null, bool save = false, bool discardChanges = false, CancellationToken cancellationToken = default) => InvokeAsync(() => documents.Close(path, save, discardChanges), cancellationToken);
    }
}
