using VsMcp.Services.VisualStudio;
using VsMcp.Services.Execution;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using VsMcp.Contracts;
using VsMcp.Mcp;

namespace VsMcp.Tools
{
    /// <summary>Runs C# in the Visual Studio process for development.</summary>
    internal sealed class CodeTools
    {
        private readonly VsContext _vs;
        private readonly DTE2 _dte;
        private readonly JoinableTaskFactory _joinableTaskFactory;

        public CodeTools(VsContext vs)
        {
            _vs = vs;
            _dte = vs.Dte;
            _joinableTaskFactory = vs.JoinableTaskFactory;
        }

        [McpServerTool(Name = "execute_code", Title = "Execute C# in Visual Studio", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
        [Description("Execute a synchronous C# method body on the VS UI thread. Available parameters: DTE (DTE2), Solution (IVsSolution), Services (IServiceProvider). Return a JSON-serializable value with return; use fully qualified names for other types. Enabled by default. Arbitrary code runs with the user's privileges, can modify VS and files, and bypasses other tools' restrictions. No sandbox, timeout or rollback. Return small data objects, not COM objects. Do not retry after an interrupted response without checking effects.")]
        public async Task<CallToolResult> ExecuteCodeTool([Description("C# method body, for example: return new { version = DTE.Version };"), MinLength(1)] string code, CancellationToken cancellationToken = default)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return ToolJson.Result(new JObject { ["instance"] = _vs.Instance(), ["result"] = CodeExecutor.Execute(code, _dte, cancellationToken) });
        }
    }
}
