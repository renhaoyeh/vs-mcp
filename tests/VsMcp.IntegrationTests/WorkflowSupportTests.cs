using System.Text.Json;
using VsMcp.Contracts;
using Xunit;

namespace VsMcp.IntegrationTests;

[Collection("Fixture")]
public sealed class WorkflowSupportTests(FixtureSession session)
{
    [Fact]
    public async Task Sets_multiple_startup_projects_rejects_ambiguous_patches_and_restores()
    {
        var client = session.Client;
        var original = (await client.Call<JsonElement>("startup_project_get")).GetProperty("data").GetProperty("projects").EnumerateArray().Select(p => p.GetString()).ToArray();
        var configuration = await client.Call<JsonElement>("get_solution_configurations");
        try
        {
            var result = (await client.Call<JsonElement>("startup_project_set", new { projects = new[] { "A", "D" } })).GetProperty("data");
            var selected = result.GetProperty("projects").EnumerateArray().Select(p => p.GetString()).ToArray();
            Assert.Equal(2, selected.Length);
            Assert.Equal(2, selected.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(original, result.GetProperty("previous").EnumerateArray().Select(p => p.GetString()));
            Assert.Contains("INVALID_ARGUMENT", await client.Fail("startup_project_set", new { project = "A", projects = new[] { "D" } }));
            Assert.Contains("INVALID_ARGUMENT", await client.Fail("startup_project_set", new { projects = new[] { "A", "A" } }));
            Assert.Equal(selected, (await client.Call<JsonElement>("startup_project_get")).GetProperty("data").GetProperty("projects").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal(configuration.GetRawText(), (await client.Call<JsonElement>("get_solution_configurations")).GetRawText());
            var cleared = await client.Call<JsonElement>("startup_project_set", new { projects = Array.Empty<string>() });
            Assert.Empty(cleared.GetProperty("data").GetProperty("projects").EnumerateArray());
        }
        finally
        {
            var restored = await client.Call<JsonElement>("startup_project_set", new { projects = original });
            Assert.Equal(original, restored.GetProperty("data").GetProperty("projects").EnumerateArray().Select(p => p.GetString()));
        }
    }

    [Fact]
    public async Task Reads_compiler_error_code_and_source_from_native_error_table()
    {
        var client = session.Client;
        var build = await client.Call<BuildOutput>("build_projects", new { projects = new[] { "C" }, operation = "rebuild" });
        Assert.Equal(BuildStatus.Failed, build.Status);
        JsonElement data = default;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            data = (await client.Call<JsonElement>("errors_list", new { severity = "error", code = "CS0029", source = "build" })).GetProperty("data");
            if (data.GetProperty("items").GetArrayLength() != 0) break;
            await Task.Delay(100);
        }
        Assert.Equal("vsTable", data.GetProperty("backend").GetString());
        Assert.True(data.GetProperty("extendedFieldsAvailable").GetBoolean());
        var error = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal("CS0029", error.GetProperty("code").GetString());
        Assert.Equal("Build", error.GetProperty("source").GetString());
        Assert.EndsWith("Class1.cs", error.GetProperty("path").GetString());
        Assert.Equal(6, error.GetProperty("line").GetInt32());
        var missing = await client.Call<JsonElement>("errors_list", new { code = "MISSING_CODE" });
        Assert.Empty(missing.GetProperty("data").GetProperty("items").EnumerateArray());
    }
}
