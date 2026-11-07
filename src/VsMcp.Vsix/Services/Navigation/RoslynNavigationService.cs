using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using VsMcp.Contracts;

namespace VsMcp.Services.Navigation
{
    // Resolves the workspace lazily so optional Roslyn services are not needed at server startup.
    // Only immutable Roslyn snapshots cross the UI-thread boundary; never DTE/COM objects.
    internal sealed class RoslynNavigationService : INavigationService
    {
        private readonly Func<Workspace> getWorkspace;
        public RoslynNavigationService(Func<Workspace> workspace) { getWorkspace = workspace; }

        public static Workspace GetWorkspace()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            return componentModel?.GetService<Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace>()
                ?? throw new NotSupportedException("The Visual Studio Roslyn workspace is unavailable.");
        }

        public async Task<NavigationResult> FindAsync(string path, int line, int column, bool references, int maxResults, string project, CancellationToken cancellationToken)
        {
            if (!Path.IsPathRooted(path) || line < 1 || column < 1 || maxResults < 1 || maxResults > 1000)
                throw new ArgumentException("Absolute path, one-based line/column and maxResults 1-1000 are required.");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var solution = getWorkspace().CurrentSolution;
            var fullPath = Path.GetFullPath(path);
            var documents = solution.GetDocumentIdsWithFilePath(fullPath).Select(solution.GetDocument)
                .Where(d => d != null && (project == null || string.Equals(d.Project.Name, project, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Project.FilePath, project, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (documents.Length == 0) throw new NotSupportedException("No matching C#/VB workspace document. C++, external files and unloaded projects are not supported by this navigation backend.");
            if (documents.Length != 1) throw new ArgumentException("The file belongs to multiple projects. Specify an exact project name or project file path.");
            var document = documents[0];
            if (document.Project.Language != LanguageNames.CSharp && document.Project.Language != LanguageNames.VisualBasic)
                throw new NotSupportedException("Structured navigation currently supports C# and Visual Basic only.");

            return await Task.Run(async () =>
            {
                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (line > text.Lines.Count || column > text.Lines[line - 1].Span.Length + 1) throw new ArgumentException("Position is outside the document.");
                var position = text.Lines[line - 1].Start + column - 1;
                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false);
                if (symbol == null) throw new ArgumentException("There is no symbol at this position.");
                symbol = await SymbolFinder.FindSourceDefinitionAsync(symbol, solution, cancellationToken).ConfigureAwait(false) ?? symbol;
                var result = new NavigationResult { Symbol = symbol.ToDisplayString(), Kind = symbol.Kind.ToString(), Language = document.Project.Language, HasMetadataDefinition = symbol.Locations.Any(l => l.IsInMetadata) };
                var locations = symbol.Locations.Where(l => l.IsInSource);
                if (references)
                {
                    var found = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
                    locations = found.SelectMany(s => s.Locations).Select(l => l.Location).Where(l => l.IsInSource);
                }
                var rows = locations.Select(l => l.GetLineSpan()).Where(s => !string.IsNullOrEmpty(s.Path))
                    .GroupBy(s => new { s.Path, s.StartLinePosition, s.EndLinePosition }).Select(g => g.First())
                    .OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.StartLinePosition.Line).ThenBy(s => s.StartLinePosition.Character)
                    .Take(maxResults + 1).ToArray();
                result.Truncated = rows.Length > maxResults;
                result.Locations = rows.Take(maxResults).Select(s => new SourceLocation { Path = s.Path, Line = s.StartLinePosition.Line + 1, Column = s.StartLinePosition.Character + 1, EndLine = s.EndLinePosition.Line + 1, EndColumn = s.EndLinePosition.Character + 1 }).ToList();
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
