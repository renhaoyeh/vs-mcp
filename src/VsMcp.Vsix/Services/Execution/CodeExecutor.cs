using VsMcp.Services.Execution;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using VsMcp.Mcp;

namespace VsMcp.Services.Execution
{
    internal static class CodeExecutor
    {
        public static JToken Execute(string code, DTE2 dte, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(code))
                throw new McpToolException(ToolErrorCode.InvalidArgument, "code must be a non-empty C# method body.");

            var services = ServiceProvider.GlobalProvider;
            var solution = (IVsSolution)services.GetService(typeof(SVsSolution));
            // Ensure the wrapper's parameter assemblies are loaded before collecting references.
            var required = new[] { typeof(object).Assembly, typeof(DTE2).Assembly,
                typeof(EnvDTE.DTE).Assembly, typeof(IVsSolution).Assembly,
                typeof(IServiceProvider).Assembly, typeof(JToken).Assembly };
            var references = AppDomain.CurrentDomain.GetAssemblies().Concat(required)
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => a.Location).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path));
            var source = "using System; using System.Linq; using System.Collections.Generic; " +
                "public static class Snippet { public static object Run(EnvDTE80.DTE2 DTE, " +
                "Microsoft.VisualStudio.Shell.Interop.IVsSolution Solution, System.IServiceProvider Services) {\n" +
                "#line 1 \"snippet.cs\"\n" + code + "\n} }";
            var compilation = CSharpCompilation.Create("VsSnippet_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using (var stream = new MemoryStream())
            {
                var emitted = compilation.Emit(stream, cancellationToken: cancellationToken);
                if (!emitted.Success)
                    throw new McpToolException(ToolErrorCode.CompileError, "C# compilation failed.",
                        new JObject { ["diagnostics"] = new JArray(emitted.Diagnostics
                            .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())) });
                cancellationToken.ThrowIfCancellationRequested();
                var assembly = Assembly.Load(stream.ToArray());
                object result;
                try
                {
                    result = assembly.GetType("Snippet").GetMethod("Run").Invoke(null, new object[] { dte, solution, services });
                }
                catch (TargetInvocationException ex)
                {
                    throw new McpToolException(ToolErrorCode.ExecutionError, ex.InnerException?.ToString() ?? ex.ToString());
                }
                try { return result == null ? JValue.CreateNull() : JToken.FromObject(result); }
                catch (Exception ex)
                {
                    throw new McpToolException(ToolErrorCode.SerializationError, "Code executed but its return value could not be serialized: " + ex.Message);
                }
            }
        }
    }
}
