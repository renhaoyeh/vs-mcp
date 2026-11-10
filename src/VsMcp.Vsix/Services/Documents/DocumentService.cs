using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using VsMcp.Contracts;
namespace VsMcp.Services.Documents
{
    // Synchronous DTE boundary: callers enter the UI thread; no MCP types belong here.
    internal sealed class DocumentService
    {
        private readonly DTE2 dte;
        public DocumentService(DTE2 automation) { dte = automation; }
        private Document Resolve(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (path != null && !Path.IsPathRooted(path)) throw new ArgumentException("Document path must be absolute.");
            return (path == null ? dte.ActiveDocument : dte.Documents.Cast<Document>().FirstOrDefault(d => { ThreadHelper.ThrowIfNotOnUIThread(); return string.Equals(d.FullName, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase); })) ?? throw new ArgumentException("Document is not open. Use document_open first.");
        }
        private static TextDocument Text(Document document) { ThreadHelper.ThrowIfNotOnUIThread(); return document.Object("TextDocument") as TextDocument ?? throw new ArgumentException("Document has no text buffer."); }
        private static string Content(TextDocument text) { ThreadHelper.ThrowIfNotOnUIThread(); return text.StartPoint.CreateEditPoint().GetText(text.EndPoint); }
        internal static string Hash(string content) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-", "").ToLowerInvariant(); }
        private static DocumentState State(Document document, string hash = null) { ThreadHelper.ThrowIfNotOnUIThread(); return new DocumentState { Path = document.FullName, Saved = document.Saved, ReadOnly = document.ReadOnly, Sha256 = hash }; }
        public DocumentList List() { ThreadHelper.ThrowIfNotOnUIThread(); return new DocumentList { Documents = dte.Documents.Cast<Document>().Select(d => { ThreadHelper.ThrowIfNotOnUIThread(); return State(d); }).ToList() }; }
        public DocumentState Active() { ThreadHelper.ThrowIfNotOnUIThread(); return dte.ActiveDocument == null ? null : State(dte.ActiveDocument); }
        public EditorSelection Selection(int maxCharacters)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (maxCharacters < 1 || maxCharacters > 200000) throw new ArgumentException("maxCharacters must be between 1 and 200000.");
            var document = dte.ActiveDocument;
            if (document == null) return null;
            TextDocument text;
            try { text = document.Object("TextDocument") as TextDocument; }
            catch (COMException) { return null; }
            if (text == null) return null;
            var selection = text.Selection;
            var start = selection.TopPoint; var end = selection.BottomPoint; var caret = selection.ActivePoint;
            var blockSelection = selection.Mode == vsSelectionMode.vsSelectionModeBox;
            var selected = ""; var truncated = false;
            if (!blockSelection && !selection.IsEmpty)
            {
                var limit = start.CreateEditPoint(); limit.CharRight(maxCharacters);
                if (limit.GreaterThan(end)) limit.MoveToPoint(end);
                selected = start.CreateEditPoint().GetText(limit);
                truncated = limit.LessThan(end) || selected.Length > maxCharacters;
                if (selected.Length > maxCharacters) selected = selected.Substring(0, maxCharacters);
            }
            return new EditorSelection { Path = document.FullName, Line = caret.Line, Column = caret.LineCharOffset,
                SelectionStartLine = start.Line, SelectionStartColumn = start.LineCharOffset,
                SelectionEndLine = end.Line, SelectionEndColumn = end.LineCharOffset, SelectedText = selected, Truncated = truncated, BlockSelection = blockSelection };
        }
        public DocumentState Open(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!Path.IsPathRooted(path) || !File.Exists(path)) throw new ArgumentException("An existing absolute file path is required.");
            dte.ItemOperations.OpenFile(path); return State(Resolve(path));
        }
        public DocumentContent Read(string path, int startLine, int maxLines, int maxCharacters)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (startLine < 1 || maxLines < 1 || maxLines > 5000 || maxCharacters < 1 || maxCharacters > 200000) throw new ArgumentException("Invalid read bounds.");
            var document = Resolve(path); var text = Text(document);
            if (startLine > text.EndPoint.Line) throw new ArgumentException("startLine is outside the document.");
            var content = Content(text); var start = text.StartPoint.CreateEditPoint(); start.MoveToLineAndOffset(startLine, 1);
            var end = text.EndPoint.CreateEditPoint();
            if ((long)startLine + maxLines <= text.EndPoint.Line) end.MoveToLineAndOffset(startLine + maxLines, 1);
            var selected = start.GetText(end); var truncated = !end.AtEndOfDocument || selected.Length > maxCharacters;
            if (selected.Length > maxCharacters) selected = selected.Substring(0, maxCharacters);
            return new DocumentContent { Path = document.FullName, Text = selected, Sha256 = Hash(content), StartLine = startLine, TotalLines = text.EndPoint.Line, Truncated = truncated };
        }
        public DocumentEditResult Write(string path, string content, string expectedSha256, bool save)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); var document = Resolve(path); var text = Text(document); Check(document, Content(text), expectedSha256);
            ReplaceBuffer(text, content); if (save) SaveDocument(document);
            return new DocumentEditResult { Document = State(document, Hash(Content(text))), Replacements = 1 };
        }
        public DocumentEditResult Replace(string path, string oldText, string newText, string expectedSha256, bool replaceAll)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(oldText)) throw new ArgumentException("oldText must not be empty.");
            var document = Resolve(path); var text = Text(document); var current = Content(text); Check(document, current, expectedSha256);
            int first = current.IndexOf(oldText, StringComparison.Ordinal);
            if (first < 0) throw new ArgumentException("oldText does not match the buffer.");
            if (!replaceAll && current.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0) throw new ArgumentException("oldText is ambiguous. Supply more context or explicitly set replaceAll.");
            int count = 0, offset = 0, found; var output = new StringBuilder();
            while ((found = current.IndexOf(oldText, offset, StringComparison.Ordinal)) >= 0) { output.Append(current, offset, found - offset).Append(newText); offset = found + oldText.Length; count++; if (!replaceAll) break; }
            output.Append(current, offset, current.Length - offset); ReplaceBuffer(text, output.ToString());
            return new DocumentEditResult { Document = State(document, Hash(Content(text))), Replacements = count };
        }
        private static void Check(Document document, string current, string expected)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (document.ReadOnly) throw new InvalidOperationException("Document is read-only.");
            if (string.IsNullOrWhiteSpace(expected) || !string.Equals(Hash(current), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Document changed or expectedSha256 is missing. Read the buffer again before editing.");
        }
        private void ReplaceBuffer(TextDocument text, string value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dte.UndoContext.IsOpen) throw new InvalidOperationException("Another editor undo transaction is open.");
            dte.UndoContext.Open("MCP document edit");
            try { text.StartPoint.CreateEditPoint().ReplaceText(text.EndPoint, value, (int)vsEPReplaceTextOptions.vsEPReplaceTextKeepMarkers); } finally { dte.UndoContext.Close(); }
        }
        private static void SaveDocument(Document document) { ThreadHelper.ThrowIfNotOnUIThread(); document.Save(); if (!document.Saved) throw new InvalidOperationException("Save did not complete."); }
        public DocumentState Save(string path) { ThreadHelper.ThrowIfNotOnUIThread(); var document = Resolve(path); SaveDocument(document); return State(document); }
        public DocumentState Close(string path, bool save, bool discardChanges)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); var document = Resolve(path);
            if (save && discardChanges) throw new ArgumentException("Choose save or discardChanges.");
            if (!document.Saved && !save && !discardChanges) throw new InvalidOperationException("Document has unsaved changes.");
            if (save) SaveDocument(document); var state = State(document); document.Close(vsSaveChanges.vsSaveChangesNo); return state;
        }
    }
}
