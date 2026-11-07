using VsMcp.Services.VisualStudio;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VsMcp.Contracts;
using VsMcp.Mcp;
using VsMcp.Services.Navigation;

namespace VsMcp.Tools
{
    internal sealed class NavigationTools
    {
        private readonly VsContext vs;
        private readonly INavigationService navigation;
        public NavigationTools(VsContext context, INavigationService service) { vs = context; navigation = service; }

        [McpServerTool(Name = "goto_definition", Title = "Find source definitions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Return source definition file/line/column locations for the symbol at a one-based document position. C# uses the live Roslyn workspace; C++ uses the installed native Peek provider and may open the file without showing Peek UI. Unsupported provider capabilities fail explicitly. Metadata-only symbols can have no source locations.")]
        public Task<ToolResponse<NavigationResult>> DefinitionAsync(string path, [Range(1, int.MaxValue)] int line, [Range(1, int.MaxValue)] int column, [Range(1, 1000)] int maxResults = 100, [Description("Exact project name or absolute project file path to disambiguate linked files.")] string project = null, CancellationToken cancellationToken = default)
            => FindAsync(path, line, column, false, maxResults, project, cancellationToken);

        [McpServerTool(Name = "find_references", Title = "Find symbol references", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Return bounded source reference file/line/column locations. C# uses the live Roslyn solution; C++ requires an installed native Peek References provider. If the native provider lacks References, returns NOT_SUPPORTED rather than textual matches or command acceptance. Does not open a results window.")]
        public Task<ToolResponse<NavigationResult>> ReferencesAsync(string path, [Range(1, int.MaxValue)] int line, [Range(1, int.MaxValue)] int column, [Range(1, 1000)] int maxResults = 100, string project = null, CancellationToken cancellationToken = default)
            => FindAsync(path, line, column, true, maxResults, project, cancellationToken);

        private async Task<ToolResponse<NavigationResult>> FindAsync(string path, int line, int column, bool references, int maxResults, string project, CancellationToken token)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var instance = vs.ReadInstance();
            try { return new ToolResponse<NavigationResult> { Instance = instance, Data = await navigation.FindAsync(path, line, column, references, maxResults, project, token) }; }
            catch (NotSupportedException e) { throw new McpToolException(ToolErrorCode.NotSupported, e.Message, ToolJson.ToJObject(new { instance })); }
            catch (ArgumentException e) { throw new McpToolException(ToolErrorCode.InvalidArgument, e.Message, ToolJson.ToJObject(new { instance })); }
        }
    }
}
