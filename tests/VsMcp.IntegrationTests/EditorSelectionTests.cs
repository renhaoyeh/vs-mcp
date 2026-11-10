using System.Text.Json;
using Xunit;

namespace VsMcp.IntegrationTests;

[Collection("Fixture")]
public sealed class EditorSelectionTests(FixtureSession session)
{
    [Fact]
    public async Task Reads_empty_and_bounded_unsaved_selections_in_both_directions()
    {
        var client = session.Client;
        var info = await client.Call<InstanceInfo>("get_vs_info");
        var path = Path.Combine(Path.GetDirectoryName(info.SolutionPath!)!, "A", "Class1.cs");
        await client.Call<ToolResponse<DocumentState>>("document_open", new { path });
        var read = (await client.Call<ToolResponse<DocumentContent>>("document_read", new { path })).Data!;
        const string content = "unsaved selection sample";
        await client.Call<ToolResponse<DocumentEditResult>>("document_write", new { path, text = content, expectedSha256 = read.Sha256 });
        try
        {
            var encoded = JsonSerializer.Serialize(path);
            async Task Position(int anchor, int active) => await client.Call<JsonElement>("execute_code", new
            {
                code = "var document = DTE.Documents.Cast<EnvDTE.Document>().First(d => d.FullName == " + encoded + "); var selection = ((EnvDTE.TextDocument)document.Object(\"TextDocument\")).Selection; selection.MoveToLineAndOffset(1, " + anchor + "); selection.MoveToLineAndOffset(1, " + active + ", true); return true;"
            });

            await Position(1, 1);
            var empty = (await client.Call<ToolResponse<EditorSelection>>("editor_selection_get")).Data!;
            Assert.Equal("", empty.SelectedText); Assert.False(empty.Truncated); Assert.False(empty.BlockSelection);
            Assert.Equal(empty.SelectionStartLine, empty.SelectionEndLine); Assert.Equal(empty.SelectionStartColumn, empty.SelectionEndColumn);

            foreach (var (anchor, active) in new[] { (1, 9), (9, 1) })
            {
                await Position(anchor, active);
                var result = await client.Call<ToolResponse<EditorSelection>>("editor_selection_get");
                Assert.Equal(path, result.Data!.Path); Assert.Equal("unsaved ", result.Data.SelectedText);
                Assert.Equal(1, result.Data.SelectionStartColumn); Assert.Equal(9, result.Data.SelectionEndColumn);
                Assert.False(result.Data.Truncated); Assert.False(result.Data.BlockSelection);
                Assert.True(result.Instance.Pid > 0);
                var bounded = (await client.Call<ToolResponse<EditorSelection>>("editor_selection_get", new { maxCharacters = 4 })).Data!;
                Assert.Equal("unsa", bounded.SelectedText); Assert.True(bounded.Truncated);
            }
        }
        finally { await client.Call<ToolResponse<DocumentState>>("document_close", new { path, discardChanges = true }); }
    }
}
