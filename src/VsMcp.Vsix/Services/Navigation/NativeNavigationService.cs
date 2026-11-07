using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using VsMcp.Contracts;

namespace VsMcp.Services.Navigation
{
    // Uses the native language's actual Peek providers, not text matching or Roslyn.
    // A session is created as query context but never started/displayed in the UI.
    internal sealed class NativeNavigationService : INavigationService
    {
        public async Task<NavigationResult> FindAsync(string path, int line, int column, bool references, int maxResults, string project, CancellationToken cancellationToken)
        {
            if (!Path.IsPathRooted(path) || !File.Exists(path) || line < 1 || column < 1 || maxResults < 1 || maxResults > 1000) throw new ArgumentException("Existing absolute file path, one-based position and maxResults 1-1000 are required.");
            if (project != null) throw new NotSupportedException("Native navigation uses the file's active VS project context; project overrides are supported only by the managed backend.");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var components = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel ?? throw new NotSupportedException("VS component model is unavailable.");
            IVsUIHierarchy hierarchy; uint itemId; IVsWindowFrame frame; IVsTextView nativeView;
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, VSConstants.LOGVIEWID_Code, out hierarchy, out itemId, out frame, out nativeView);
            var view = components.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(nativeView) ?? throw new NotSupportedException("The file has no native text view.");
            var snapshot = view.TextSnapshot;
            if (!snapshot.ContentType.IsOfType("C/C++")) throw new NotSupportedException("Native navigation supports C/C++ editors only.");
            if (line > snapshot.LineCount || column > snapshot.GetLineFromLineNumber(line - 1).Length + 1) throw new ArgumentException("Position is outside the buffer.");
            var position = snapshot.GetLineFromLineNumber(line - 1).Start.Position + column - 1;
            var point = snapshot.CreateTrackingPoint(position, PointTrackingMode.Positive);
            var relation = references ? "References" : PredefinedPeekRelationships.Definitions.Name;
            var session = components.GetService<IPeekBroker>().CreatePeekSession(new PeekSessionCreationOptions(view, relation, point, null, false, null, false));
            if (session == null) throw new NotSupportedException("The native language service could not create a navigation query. Check C++ workload and IntelliSense readiness.");
            var items = new List<IPeekableItem>(); var sources = new List<IPeekableItemSource>();
            try
            {
                var providers = components.DefaultExportProvider.GetExports<IPeekableItemSourceProvider, IDictionary<string, object>>();
                foreach (var provider in providers)
                {
                    if (!provider.Metadata.TryGetValue("ContentTypes", out var metadata) || !(metadata is IEnumerable<string> contentTypes) || !contentTypes.Any(snapshot.ContentType.IsOfType)) continue;
                    var source = provider.Value.TryCreatePeekableItemSource(view.TextBuffer);
                    if (source == null) continue;
                    sources.Add(source); source.AugmentPeekSession(session, items);
                }
                if (items.Count == 0) throw new NotSupportedException("The installed C++ language service supplies no " + relation + " query at this position. No textual fallback was used.");
                var result = new NavigationResult { Symbol = string.Join("; ", items.Select(i => i.DisplayName).Distinct()), Kind = references ? "References" : "Definitions", Language = "C++" };
                var locations = new List<SourceLocation>();
                var queried = false;
                foreach (var item in items)
                {
                    if (!item.Relationships.Any(r => string.Equals(r.Name, relation, StringComparison.OrdinalIgnoreCase))) continue;
                    var source = item.GetOrCreateResultSource(relation); if (source == null) continue;
                    queried = true;
                    var rows = new Results(); var callback = new Callback();
                    await Task.Run(() => source.FindResults(relation, rows, cancellationToken, callback), cancellationToken);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    if (callback.Error != null) throw new InvalidOperationException("Native symbol query failed: " + callback.Error.Message, callback.Error);
                    foreach (var row in rows.OfType<IDocumentPeekResult>())
                    {
                        var span = row.IdentifyingSpan;
                        if (span.TryGetStartLineIndex(out var startLine, out var startIndex) && span.TryGetEndLineIndex(out var endLine, out var endIndex))
                            locations.Add(new SourceLocation { Path = row.FilePath, Line = startLine + 1, Column = startIndex + 1, EndLine = endLine + 1, EndColumn = endIndex + 1 });
                    }
                }
                if (!queried) throw new NotSupportedException("The installed native provider does not implement the " + relation + " relationship.");
                var unique = locations.GroupBy(l => new { l.Path, l.Line, l.Column, l.EndLine, l.EndColumn }).Select(g => g.First()).OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Line).ThenBy(l => l.Column).ToArray();
                result.Truncated = unique.Length > maxResults; result.Locations = unique.Take(maxResults).ToList(); return result;
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                foreach (var source in sources) source.Dispose();
                session.Dismiss();
            }
        }
        private sealed class Callback : IFindPeekResultsCallback
        {
            public Exception Error;
            public void ReportProgress(int percentProgress) { }
            public void ReportFailure(Exception failure) { Error = failure; }
        }
        private sealed class Results : Collection<IPeekResult>, IPeekResultCollection
        {
            public int IndexOf(IPeekResult item, int startIndex) { for (int i = startIndex; i < Count; i++) if (ReferenceEquals(this[i], item)) return i; return -1; }
            public void Move(int oldIndex, int newIndex) { var item = this[oldIndex]; RemoveAt(oldIndex); Insert(newIndex, item); }
        }
    }
}
