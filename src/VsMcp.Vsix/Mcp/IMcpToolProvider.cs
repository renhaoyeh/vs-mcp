using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace VsMcp.Mcp
{
    /// <summary>
    /// A source of tools. Tools are ordinary methods marked with <see cref="McpServerToolAttribute"/>; the official SDK
    /// derives each tool's input and output schema from the method's parameters, return type and
    /// <see cref="System.ComponentModel.DescriptionAttribute"/>, and invokes it.
    /// </summary>
    internal interface IMcpToolProvider
    {
        IReadOnlyList<McpServerTool> Tools { get; }
    }

    /// <summary>The serialization rules every tool shares: the SDK's camelCase names and string enums.</summary>
    internal static class ToolJson
    {
        public static readonly JsonSerializerOptions Options = Create();

        private static JsonSerializerOptions Create()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return options;
        }

        /// <summary>Creates the SDK tools for every public method marked <see cref="McpServerToolAttribute"/> on the <paramref name="targets"/>.</summary>
        public static IReadOnlyList<McpServerTool> CreateTools(params object[] targets) =>
            targets
                .SelectMany(target => target.GetType().GetMethods().Select(method => (target, method)))
                .Where(pair => pair.method.GetCustomAttributes(typeof(McpServerToolAttribute), false).Length > 0)
                .Select(pair => McpServerTool.Create(pair.method, pair.target, new McpServerToolCreateOptions { SerializerOptions = Options }))
                .ToList();

        /// <summary>A result whose structured content is <paramref name="value"/>, for tools that build their result as JSON.</summary>
        public static CallToolResult Result(JToken value)
        {
            var camel = ToCamelCase(value);
            var result = new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = camel.ToString(Newtonsoft.Json.Formatting.Indented) } },
            };
            if (camel is JObject)
            {
                using (var document = JsonDocument.Parse(camel.ToString(Newtonsoft.Json.Formatting.None)))
                {
                    result.StructuredContent = document.RootElement.Clone();
                }
            }

            return result;
        }

        /// <summary>A copy of <paramref name="token"/> whose property names are camelCase (snake_case names are converted).</summary>
        public static JToken ToCamelCase(JToken token)
        {
            switch (token)
            {
                case JObject obj:
                    var copy = new JObject();
                    foreach (var property in obj.Properties()) copy[CamelCase(property.Name)] = ToCamelCase(property.Value);
                    return copy;
                case JArray array:
                    return new JArray(array.Select(ToCamelCase));
                default:
                    return token.DeepClone();
            }
        }

        public static string CamelCase(string name)
        {
            if (name.IndexOf('_') < 0) return name;
            var parts = name.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            return parts[0] + string.Concat(parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));
        }

        public static JObject ToJObject(object value) => JObject.Parse(JsonSerializer.Serialize(value, value.GetType(), Options));
    }

    /// <summary>
    /// An expected tool failure. It is an <see cref="McpException"/>, so the SDK reports its message to the caller as
    /// an error result; the message starts with the error code and ends with the structured details as JSON.
    /// </summary>
    internal sealed class McpToolException : McpException
    {
        public McpToolException(ToolErrorCode code, string message, JObject details = null)
            : base(Compose(code, message, details))
        {
            Code = code;
            Details = details;
        }

        public ToolErrorCode Code { get; }
        public JObject Details { get; }

        private static string Compose(ToolErrorCode code, string message, JObject details) =>
            JToken.FromObject(code).Value<string>()
            + ": " + message + (details == null ? "" : "\n" + ToolJson.ToCamelCase(details).ToString(Newtonsoft.Json.Formatting.None));
    }
}
