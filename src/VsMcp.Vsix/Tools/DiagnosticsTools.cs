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
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell.TableManager;
using VsMcp.Services.Diagnostics;

namespace VsMcp.Tools
{
    internal sealed class DiagnosticsTools
    {
        private readonly VsContext vs;
        public DiagnosticsTools(VsContext context) { vs = context; }
        private CallToolResult Result(object data) { ThreadHelper.ThrowIfNotOnUIThread(); return ToolJson.Result(new JObject { ["instance"] = vs.Instance(), ["data"] = JToken.Parse(System.Text.Json.JsonSerializer.Serialize(data, ToolJson.Options)) }); }
        [McpServerTool(Name = "errors_list", Title = "errors_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Read bounded diagnostics from the connected VS Error List data sources, including code, source, buildTool and producer identifiers when supplied. Severity is error/warning/message. code and source are exact case-insensitive filters; source is VS-reported Build/Other, never inferred as IntelliSense. producer matches buildTool or data-source identifier. Positions are one-based. isStable reports provider stability, not proof a background analysis finished. Missing extended fields are null. Falls back to legacy DTE with explicit backend/availability only when the table service is unavailable; extended filters are then rejected. Does not change Error List UI filters or start analysis/builds.")]
        public async Task<CallToolResult> ErrorsAsync(string severity = null, string project = null, string text = null, int maxResults = 200, string code = null, string source = null, string producer = null, CancellationToken cancellationToken = default)
        {
            if (maxResults < 1 || maxResults > 5000) throw new ArgumentOutOfRangeException(nameof(maxResults));
            if (severity != null && severity != "error" && severity != "warning" && severity != "message") throw new ArgumentException("Severity must be error, warning or message.");
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var components = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
            var manager = components?.GetService<ITableManagerProvider>()?.GetTableManager(StandardTables.ErrorsTable);
            if (manager != null)
            {
                var sources = manager.Sources.ToArray();
                var data = await Task.Run(() => ErrorTableReader.Read(sources, severity, project, text, code, source, producer, maxResults, cancellationToken), cancellationToken);
                await vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                return ToolJson.Result(new JObject { ["instance"] = vs.Instance(), ["data"] = data });
            }
            if (code != null || source != null || producer != null)
                throw new McpToolException(ToolErrorCode.NotSupported, "Extended Error List filters require the VS table data service.", new JObject { ["instance"] = vs.Instance() });
            var results = new JArray(); var items = vs.Dte.ToolWindows.ErrorList.ErrorItems;
            for (int i = 1; i <= items.Count; i++)
            {
                var item = items.Item(i); var kind = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh ? "error" : item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium ? "warning" : "message";
                if (severity != null && severity != kind || project != null && !VsContext.Same(project, item.Project) || text != null && item.Description.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (results.Count == maxResults) return Result(new { items = results, truncated = true, backend = "legacyDte", extendedFieldsAvailable = false });
                results.Add(new JObject { ["severity"] = kind, ["description"] = item.Description, ["project"] = item.Project, ["path"] = item.FileName, ["line"] = item.Line, ["column"] = item.Column, ["code"] = null, ["source"] = null, ["buildTool"] = null });
            }
            return Result(new { items = results, truncated = false, backend = "legacyDte", extendedFieldsAvailable = false });
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
