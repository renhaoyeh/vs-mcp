using Xunit;

namespace VsMcp.IntegrationTests;

/// <summary>
/// Builds in tests/VsMcp.TestProjects/VsMcp.BuildTestSolution/VsMcp.BuildTestSolution.sln. Project C contains a compile error on purpose and
/// must not be fixed. The tests write build output to the fixture's bin and obj folders.
/// </summary>
[Collection("Fixture")]
public sealed class BuildToolsTests(FixtureSession session)
{

    private static Dictionary<string, object?> Arguments(object extra)
    {
        var arguments = new Dictionary<string, object?>();
        foreach (var property in extra.GetType().GetProperties()) arguments[property.Name] = property.GetValue(extra);
        return arguments;
    }

    private static Task<BuildOutput> RunBuild(ModelContextProtocol.Client.McpClient client, object extra) =>
        client.Call<BuildOutput>("build_projects", Arguments(extra));

    [Fact]
    public async Task Builds_a_folder_in_dependency_order()
    {
        var client = session.Client;
        var result = await RunBuild(client, new { folder = "Libs" });

        Assert.Equal(BuildStatus.Succeeded, result.Status);
        Assert.True(result.Finished);
        Assert.Equal(new[] { "A", "B" }, result.Projects.Select(p => p.Project));
        Assert.All(result.Projects, project => Assert.Equal(ProjectBuildResult.Succeeded, project.Result));
        Assert.False(result.StoppedEarly);
        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public async Task Builds_a_nested_folder_and_a_whole_folder_tree()
    {
        var client = session.Client;

        var sub = await RunBuild(client, new { folder = "01 Shared Plugins/Sub" });
        Assert.Equal(new[] { "S1" }, sub.Projects.Select(p => p.Project));

        var all = await RunBuild(client, new { folder = "01 Shared Plugins" });
        Assert.Equal(BuildStatus.Succeeded, all.Status);
        Assert.Equal(new[] { "P1", "P2", "S1" }, all.Projects.Select(p => p.Project).Order());
    }

    [Fact]
    public async Task Builds_the_listed_projects_in_the_listed_order()
    {
        var client = session.Client;
        var result = await RunBuild(client, new { projects = new[] { "D", "A" } });

        Assert.Equal(new[] { "D", "A" }, result.Projects.Select(p => p.Project));
    }

    [Fact]
    public async Task Stops_at_the_first_failed_project_by_default()
    {
        var client = session.Client;

        // Visual Studio finishes an up-to-date project at once, so make D need a rebuild: it must not be reached.
        File.SetLastWriteTimeUtc(Path.Combine(session.SolutionDirectory!, "D", "Class1.cs"), DateTime.UtcNow);
        var result = await RunBuild(client, new { projects = new[] { "C", "D" } });

        Assert.Equal(BuildStatus.Failed, result.Status);
        Assert.True(result.StoppedEarly);
        Assert.Equal(ProjectBuildResult.Failed, result.Projects.Single(p => p.Project == "C").Result);
        Assert.Equal(ProjectBuildResult.Cancelled, result.Projects.Single(p => p.Project == "D").Result);
        var error = Assert.Single(result.Errors);
        Assert.EndsWith("Class1.cs", error.File);
        Assert.Equal(6, error.Line);
        Assert.Contains("Cannot implicitly convert", error.Description);
    }

    [Fact]
    public async Task Builds_the_rest_when_stopping_is_turned_off()
    {
        var client = session.Client;
        var result = await RunBuild(client, new { projects = new[] { "C", "D" }, stopOnFirstError = false });

        Assert.Equal(BuildStatus.Failed, result.Status);
        Assert.False(result.StoppedEarly);
        Assert.Equal(ProjectBuildResult.Failed, result.Projects.Single(p => p.Project == "C").Result);
        Assert.Equal(ProjectBuildResult.Succeeded, result.Projects.Single(p => p.Project == "D").Result);
    }

    [Fact]
    public async Task Builds_what_a_project_requires_first_like_Visual_Studio_does()
    {
        var client = session.Client;
        var result = await RunBuild(client, new { projects = new[] { "C" }, stopOnFirstError = false });

        // C requires B, which requires A: they are built first.
        Assert.Equal(new[] { "A", "B", "C" }, result.Projects.Select(p => p.Project));
    }

    [Fact]
    public async Task A_clean_cleans_only_the_listed_projects()
    {
        var client = session.Client;
        var result = await RunBuild(client, new { projects = new[] { "B" }, operation = "clean" });

        Assert.Equal(new[] { "B" }, result.Projects.Select(p => p.Project));
    }

    [Fact]
    public async Task Rejects_bad_requests_without_building()
    {
        var client = session.Client;

        Assert.Contains("INVALID_ARGUMENT:", await client.Fail("build_projects", Arguments(new { projects = new[] { "A" }, folder = "Libs" })));
        Assert.Contains("INVALID_ARGUMENT:", await client.Fail("build_projects", Arguments(new { })));
        var missing = await client.Fail("build_projects", Arguments(new { projects = new[] { "Nope" } }));
        Assert.Contains("PROJECT_NOT_FOUND:", missing);
        Assert.Contains("get_projects(recursive: true)", missing);
        Assert.Contains("project_reload", missing);
        Assert.Contains("after inspection", missing);
        Assert.Contains("fallback reason", missing);
        Assert.Contains("DUPLICATE_PROJECT:", await client.Fail("build_projects", Arguments(new { projects = new[] { "A", "A" } })));
        Assert.Contains("FOLDER_NOT_FOUND:", await client.Fail("build_projects", Arguments(new { folder = "Nope" })));
    }

    [Fact]
    public async Task Reports_the_latest_build_and_has_nothing_to_cancel_afterwards()
    {
        var client = session.Client;
        await RunBuild(client, new { projects = new[] { "A" } });

        var status = await client.Call<BuildOutput>("get_build_status");
        Assert.Equal(BuildStatus.Succeeded, status.Status);
        Assert.Equal(new[] { "A" }, status.Projects.Select(p => p.Project));

        var cancel = await client.Call<CancelBuildOutput>("cancel_build");
        Assert.False(cancel.CancelRequested);
        Assert.False(cancel.Running);
    }

    private string[] OutputsOf(string project) =>
        Directory.EnumerateFiles(Path.Combine(session.SolutionDirectory!, project, "bin"), project + ".dll", SearchOption.AllDirectories).ToArray();

    [Fact]
    public async Task Cleans_and_rebuilds_the_build_output_of_the_listed_projects()
    {
        var client = session.Client;

        var built = await RunBuild(client, new { projects = new[] { "A" } });
        Assert.Equal(BuildOperation.Build, built.Operation);
        Assert.NotEmpty(OutputsOf("A"));

        var cleaned = await RunBuild(client, new { projects = new[] { "A" }, operation = "clean" });
        Assert.Equal(BuildOperation.Clean, cleaned.Operation);
        Assert.Equal(BuildStatus.Succeeded, cleaned.Status);
        Assert.Empty(OutputsOf("A"));

        var rebuilt = await RunBuild(client, new { projects = new[] { "A" }, operation = "rebuild" });
        Assert.Equal(BuildOperation.Rebuild, rebuilt.Operation);
        Assert.Equal(BuildStatus.Succeeded, rebuilt.Status);
        Assert.Equal(new[] { "A" }, rebuilt.Projects.Select(p => p.Project));
        Assert.NotEmpty(OutputsOf("A"));
    }

    [Fact]
    public async Task A_rebuild_rebuilds_a_project_that_is_up_to_date()
    {
        var client = session.Client;
        await RunBuild(client, new { projects = new[] { "B" } });
        var output = OutputsOf("B").Single();
        File.SetLastWriteTimeUtc(output, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var rebuilt = await RunBuild(client, new { projects = new[] { "B" }, operation = "rebuild" });

        Assert.Equal(BuildStatus.Succeeded, rebuilt.Status);
        Assert.True(File.GetLastWriteTimeUtc(OutputsOf("B").Single()) > new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Builds_in_the_active_configuration()
    {
        var client = session.Client;
        var active = (await client.Call<GetSolutionConfigurationsOutput>("get_solution_configurations")).ActiveConfiguration!;

        var result = await RunBuild(client, new { projects = new[] { "A" } });

        Assert.Equal(BuildStatus.Succeeded, result.Status);
        Assert.Equal(active.SolutionConfiguration, result.SolutionConfiguration);
        Assert.Equal(active.SolutionPlatform, result.SolutionPlatform);
    }
}
