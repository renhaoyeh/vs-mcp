using VsMcp.Services.VisualStudio;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using VsMcp.Mcp;

namespace VsMcp.Tools
{
    internal sealed class DiagnosticsTools
    {
        private readonly VsContext vs;
        public DiagnosticsTools(VsContext context) { vs = context; }
        private CallToolResult Result(object data) { ThreadHelper.ThrowIfNotOnUIThread(); return ToolJson.Result(new JObject { ["instance"] = vs.Instance(), ["data"] = JToken.Parse(System.Text.Json.JsonSerializer.Serialize(data, ToolJson.Options)) }); }
        [McpServerTool(Name = "errors_list", Title = "errors_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Read the Visual Studio Error List with optional severity, project and description filters. Severity is error, warning or message. Results are bounded.")]
        public async Task<CallToolResult> ErrorsAsync(string severity = null, string project = null, string text = null, int maxResults = 200, CancellationToken cancellationToken = default)
        {
            if (maxResults < 1 || maxResults > 5000) throw new ArgumentOutOfRangeException(nameof(maxResults));
            if (severity != null && severity != "error" && severity != "warning" && severity != "message") throw new ArgumentException("Severity must be error, warning or message.");
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var results = new JArray(); var items = vs.Dte.ToolWindows.ErrorList.ErrorItems;
            for (int i = 1; i <= items.Count; i++)
            {
                var item = items.Item(i); var kind = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh ? "error" : item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium ? "warning" : "message";
                if (severity != null && severity != kind || project != null && !VsContext.Same(project, item.Project) || text != null && item.Description.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (results.Count == maxResults) return Result(new { items = results, truncated = true });
                results.Add(new JObject { ["severity"] = kind, ["description"] = item.Description, ["project"] = item.Project, ["path"] = item.FileName, ["line"] = item.Line, ["column"] = item.Column });
            }
            return Result(new { items = results, truncated = false });
        }
        [McpServerTool(Name = "output_list_panes", Title = "output_list_panes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List the Output window panes by name and GUID.")]
        public async Task<CallToolResult> PanesAsync(CancellationToken cancellationToken = default)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return Result(vs.Dte.ToolWindows.OutputWindow.OutputWindowPanes.Cast<OutputWindowPane>().Select(p => { ThreadHelper.ThrowIfNotOnUIThread(); return new { p.Name, p.Guid }; }).ToArray());
        }
        private OutputWindowPane Pane(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var matches = vs.Dte.ToolWindows.OutputWindow.OutputWindowPanes.Cast<OutputWindowPane>().Where(p => { ThreadHelper.ThrowIfNotOnUIThread(); return VsContext.Same(p.Name, name) || VsContext.Same(p.Guid, name); }).ToArray();
            if (matches.Length != 1) throw new ArgumentException("Specify a unique Output pane name or GUID from output_list_panes."); return matches[0];
        }
        [McpServerTool(Name = "output_read", Title = "output_read", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Read the bounded tail of an Output pane by exact name or GUID.")]
        public async Task<CallToolResult> OutputAsync(string pane, int maxCharacters = 20000, CancellationToken cancellationToken = default)
        {
            if (maxCharacters < 1 || maxCharacters > 200000) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var p = Pane(pane); var doc = p.TextDocument; var start = doc.EndPoint.CreateEditPoint();
            start.CharLeft(Math.Min(maxCharacters, doc.EndPoint.AbsoluteCharOffset - 1));
            return Result(new { pane = p.Name, text = start.GetText(doc.EndPoint), truncated = !start.AtStartOfDocument });
        }
    }
}
