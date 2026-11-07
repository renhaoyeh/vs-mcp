using System.Text.Json;
using Xunit;
namespace VsMcp.IntegrationTests;

[Collection("Fixture")]
public sealed class EditorWorkflowTests(FixtureSession session)
{
    [Theory]
    [InlineData(".cs")]
    [InlineData(".cpp")]
    public async Task Buffer_edits_require_current_hash_and_preserve_unsaved_changes(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "vs-mcp-editor-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllText(path, "// alpha\n// beta\n// alpha"); var client = session.Client;
        try
        {
            await client.Call<ToolResponse<DocumentState>>("document_open", new { path });
            var read = (await client.Call<ToolResponse<DocumentContent>>("document_read", new { path })).Data;
            await client.Fail("editor_replace", new { path, oldText = "alpha", newText = "gamma", expectedSha256 = read.Sha256 });
            var edited = await client.Call<ToolResponse<DocumentEditResult>>("editor_replace", new { path, oldText = "alpha", newText = "gamma", expectedSha256 = read.Sha256, replaceAll = true });
            Assert.Equal(2, edited.Data.Replacements);
            await client.Fail("document_write", new { path, text = "stale", expectedSha256 = read.Sha256 });
            read = (await client.Call<ToolResponse<DocumentContent>>("document_read", new { path })).Data;
            Assert.Contains("gamma", read.Text); Assert.Contains("alpha", File.ReadAllText(path));
            await client.Fail("document_close", new { path });
            var bounded = (await client.Call<ToolResponse<DocumentContent>>("document_read", new { path, maxCharacters = 4 })).Data;
            Assert.Equal(4, bounded.Text.Length); Assert.True(bounded.Truncated); Assert.Equal(read.Sha256, bounded.Sha256);
            await client.Call<ToolResponse<DocumentState>>("document_save", new { path }); Assert.Contains("gamma", File.ReadAllText(path));
        }
        finally { await client.Call<ToolResponse<DocumentState>>("document_close", new { path, discardChanges = true }); File.Delete(path); }
    }
    [Fact]
    public async Task Debugger_commands_do_not_launch_an_idle_debuggee()
    {
        foreach (var name in new[] { "debugger_continue", "debugger_step_into", "debugger_step_over", "debugger_step_out", "debugger_get_locals", "debugger_get_callstack" }) await session.Client.Fail(name);
        await session.Client.Fail("debugger_evaluate", new { expression = "1 + 1" });
    }
    [Fact]
    public async Task Ide_queries_and_invalid_settings_preserve_configuration()
    {
        var client = session.Client; var before = await client.Call<JsonElement>("get_solution_configurations");
        foreach (var name in new[] { "document_list", "document_active", "startup_project_get", "output_list_panes", "errors_list" })
            Assert.True((await client.Call<JsonElement>(name)).GetProperty("instance").GetProperty("pid").GetInt32() > 0);
        await client.Fail("build_configuration_set", new { configuration = "missing", platform = "missing" });
        await client.Fail("startup_project_set", new { project = "missing" });
        Assert.Equal(before.ToString(), (await client.Call<JsonElement>("get_solution_configurations")).ToString());
    }
}
