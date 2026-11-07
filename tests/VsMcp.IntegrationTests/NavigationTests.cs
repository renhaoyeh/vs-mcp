using Xunit;
namespace VsMcp.IntegrationTests;

[Collection("Fixture")]
public sealed class NavigationTests(FixtureSession session)
{
    [Fact]
    public async Task Managed_navigation_uses_unsaved_semantic_workspace()
    {
        var client = session.Client; var info = await client.Call<InstanceInfo>("get_vs_info");
        var path = Path.Combine(Path.GetDirectoryName(info.SolutionPath!)!, "A", "Class1.cs");
        await client.Call<ToolResponse<DocumentState>>("document_open", new { path });
        try
        {
            var read = (await client.Call<ToolResponse<DocumentContent>>("document_read", new { path })).Data;
            const string content = "namespace A;\npublic class Class1 { public int Value; public int Read() => Value; }";
            await client.Call<ToolResponse<DocumentEditResult>>("document_write", new { path, text = content, expectedSha256 = read.Sha256 });
            var definitionColumn = content.Split('\n')[1].IndexOf("Value", StringComparison.Ordinal) + 1;
            var referenceColumn = content.Split('\n')[1].LastIndexOf("Value", StringComparison.Ordinal) + 1;
            var definition = (await client.Call<ToolResponse<NavigationResult>>("goto_definition", new { path, line = 2, column = referenceColumn })).Data;
            Assert.Contains(definition.Locations, l => l.Path == path && l.Line == 2 && l.Column == definitionColumn);
            var references = (await client.Call<ToolResponse<NavigationResult>>("find_references", new { path, line = 2, column = definitionColumn })).Data;
            Assert.Contains(references.Locations, l => l.Path == path && l.Column == referenceColumn);
            Assert.DoesNotContain("Value", File.ReadAllText(path));
        }
        finally { await client.Call<ToolResponse<DocumentState>>("document_close", new { path, discardChanges = true }); }
    }
}

[Collection("Native")]
public sealed class NativeWorkflowTests(NativeSession session)
{
    [Fact]
    public async Task Native_solution_builds_with_real_dependency_results()
    {
        var result = await session.Client.Call<BuildOutput>("build_projects", new { projects = new[] { "CppApp" } });
        Assert.Equal(BuildStatus.Succeeded, result.Status);
        Assert.Contains(result.Projects, p => p.Project == "CppLib");
    }

    [Fact]
    public async Task Native_definition_returns_a_source_location()
    {
        var client = session.Client; var info = await client.Call<InstanceInfo>("get_vs_info");
        var path = Path.Combine(Path.GetDirectoryName(info.SolutionPath!)!, "CppApp", "app.cpp");
        await client.Call<ToolResponse<DocumentState>>("document_open", new { path });
        // Wait for native IntelliSense to finish loading the fixture, with a bounded retry of read-only queries.
        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var result = (await client.Call<ToolResponse<NavigationResult>>("goto_definition", new { path, line = 2, column = 27 })).Data;
                Assert.Equal("C++", result.Language); Assert.NotEmpty(result.Locations); return;
            }
            catch (Exception e) { last = e; await Task.Delay(2000); }
        }
        throw new Xunit.Sdk.XunitException("Native navigation did not become ready: " + last?.Message);
    }

    [Fact]
    public async Task Native_tests_return_actual_failures()
    {
        var client = session.Client;
        var build = await client.Call<BuildOutput>("build_projects", new { projects = new[] { "CppTests" } });
        Assert.Equal(BuildStatus.Succeeded, build.Status);
        var info = await client.Call<InstanceInfo>("get_vs_info"); var folder = Path.GetDirectoryName(info.SolutionPath!)!;
        var source = Directory.EnumerateFiles(folder, "CppTests.dll", SearchOption.AllDirectories).Single();
        var run = (await client.Call<ToolResponse<TestRunResult>>("test_run", new { sources = new[] { source }, waitSeconds = 120 })).Data;
        if (run.Status == TestRunStatus.Running) run = (await client.Call<ToolResponse<TestRunResult>>("test_status", new { runId = run.RunId, waitSeconds = 120 })).Data;
        Assert.True(run.Status == TestRunStatus.Failed, run.Error + "\n" + run.Output);
        Assert.Equal(1, run.Report.Failed); Assert.Equal(1, run.Report.Passed);
        Assert.Contains(run.Report.Results, r => r.Outcome == "Failed" && !string.IsNullOrEmpty(r.ErrorMessage));
    }
}
