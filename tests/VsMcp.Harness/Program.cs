using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using VsMcp.Contracts;
using VsMcp.Mcp;

namespace VsMcp.Harness
{
    internal static class Program
    {
        private const int Port = 18771;

        private static async Task<int> Main()
        {
            var log = new List<string>();
            using (var server = new McpHttpServer(Port, new FakeTools(), message => { lock (log) log.Add(message); Console.WriteLine("[server] " + message); }))
            {
                server.Start();
                var failures = 0;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    failures += await Check(http, "ping", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
                    failures += await Check(http, "initialize", "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"harness\",\"version\":\"0\"}}}");
                    failures += await Check(http, "notifications/initialized", "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                    failures += await Check(http, "tools/list", "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}");
                    failures += await Check(http, "tools/call echo", "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"text\":\"hi\",\"repeat\":2}}}");
                    failures += await Check(http, "tools/call fail", "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"fail\",\"arguments\":{}}}");
                    failures += await Check(http, "tools/call unknown", "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\",\"arguments\":{}}}");
                    failures += await Check(http, "bad json", "{not json");
                }

                Console.WriteLine(failures == 0 ? "ALL REQUESTS ANSWERED" : failures + " request(s) did not get an answer");
                return failures == 0 ? 0 : 1;
            }
        }

        private static async Task<int> Check(HttpClient http, string label, string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/mcp")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");

            var started = DateTime.UtcNow;
            try
            {
                var response = await http.SendAsync(request).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Console.WriteLine($"--- {label}: HTTP {(int)response.StatusCode} {response.Content.Headers.ContentType} in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
                Console.WriteLine(text.Length > 14000 ? text.Substring(0, 14000) + "..." : text.Trim());
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"--- {label}: NO ANSWER ({ex.GetType().Name}: {ex.Message}) after {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
                return 1;
            }
        }

        /// <summary>Fake tools declared the same way as the real ones: attributed methods the SDK turns into tools.</summary>
        private sealed class FakeTools : IMcpToolProvider
        {
            public FakeTools()
            {
                Tools = ToolJson.CreateTools(this);
            }

            public IReadOnlyList<McpServerTool> Tools { get; }

            [McpServerTool(Name = "echo", Title = "Echo", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
            [Description("Returns its text argument.")]
            public EchoResult Echo(
                [Description("Text to return.")] string text,
                [Description("How many times to repeat it."), Range(1, 5)] int repeat = 1) =>
                new EchoResult { Text = string.Concat(Enumerable.Repeat(text, repeat)) };

            [McpServerTool(Name = "fail", Title = "Fail", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
            [Description("Always reports an expected failure.")]
            public string Fail() => throw new McpToolException(ToolErrorCode.InvalidArgument, "Expected failure.", new JObject { ["available_values"] = new JArray("a", "b") });

            [McpServerTool(Name = "sample_build", Title = "Sample build result", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
            [Description("Returns a sample build result so the production output schema can be inspected.")]
            public BuildOutput SampleBuild() => new BuildOutput { Status = BuildStatus.Failed, Projects = { new BuildProjectOutput { Project = "P", Result = ProjectBuildResult.NotBuilt } } };
        }

        public sealed class EchoResult
        {
            [Description("The repeated text.")]
            public string Text { get; set; }
        }
    }
}
