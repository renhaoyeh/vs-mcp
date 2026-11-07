global using VsMcp.Contracts;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Sdk;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VsMcp.IntegrationTests;

/// <summary>The serialization the server uses for tool results: camelCase names and camelCase string enums.</summary>
internal static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

internal static class ToolCalls
{
    private static IReadOnlyDictionary<string, object?> Arguments(object? arguments) =>
        arguments switch
        {
            null => new Dictionary<string, object?>(),
            IReadOnlyDictionary<string, object?> dictionary => dictionary,
            _ => arguments.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(property => property.Name, property => property.GetValue(arguments)),
        };

    /// <summary>The longest any single tool call may take; a build waits at most 600 s itself, so this only catches a hung instance.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(180);

    private static async Task<CallToolResult> Invoke(McpClient client, string name, object? arguments)
    {
        using var timeout = new CancellationTokenSource(CallTimeout);
        try
        {
            return await client.CallToolAsync(name, Arguments(arguments), cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new XunitException($"{name} did not answer within {CallTimeout.TotalSeconds:0} s; Visual Studio looks hung.");
        }
    }

    public static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    /// <summary>Calls a tool that must succeed and reads its structured result as <typeparamref name="T"/>.</summary>
    public static async Task<T> Call<T>(this McpClient client, string name, object? arguments = null)
    {
        var result = await Invoke(client, name, arguments);
        if (result.IsError == true) throw new XunitException($"{name} failed: {TextOf(result)}");
        var structured = result.StructuredContent ?? throw new XunitException($"{name} returned no structured content: {TextOf(result)}");
        return structured.Deserialize<T>(Json.Options)!;
    }

    /// <summary>Calls a tool that must fail and returns the error text: "CODE: message" and then the details as JSON.</summary>
    public static async Task<string> Fail(this McpClient client, string name, object? arguments = null)
    {
        var result = await Invoke(client, name, arguments);
        Assert.True(result.IsError == true, $"{name} was expected to fail but returned: {TextOf(result)}");
        return TextOf(result);
    }
}
