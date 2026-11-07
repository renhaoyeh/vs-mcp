using Xunit;

namespace VsMcp.IntegrationTests;

/// <summary>Queries against tests/VsMcp.TestProjects/VsMcp.BuildTestSolution/VsMcp.BuildTestSolution.sln: projects, folders, configurations and dependencies.</summary>
[Collection("Fixture")]
public sealed class SolutionToolsTests(FixtureSession session)
{

    [Fact]
    public async Task Reports_the_instance_and_its_solution()
    {
        var client = session.Client;
        var info = await client.Call<InstanceInfo>("get_vs_info");

        Assert.True(info.Pid > 0);
        Assert.True(info.SolutionOpen);
        Assert.EndsWith("VsMcp.BuildTestSolution.sln", info.SolutionPath);
    }

    [Fact]
    public async Task Lists_the_top_level_with_folder_counts()
    {
        var client = session.Client;
        var root = await client.Call<GetProjectsOutput>("get_projects");

        Assert.Null(root.Folder);
        Assert.Equal(new[] { "Libs", "01 Shared Plugins", "02 Apps" }, root.Folders.Select(f => f.Name));
        Assert.Equal(new[] { 2, 3, 1 }, root.Folders.Select(f => f.TotalProjectCount));
        Assert.Equal(new[] { "C", "D", "Root" }, root.Projects.Select(p => p.Name).Order());
        Assert.All(root.Folders, folder => Assert.Null(folder.Folders));
    }

    [Fact]
    public async Task Drills_into_a_folder_and_expands_recursively()
    {
        var client = session.Client;

        var folder = await client.Call<GetProjectsOutput>("get_projects", new { folder = "01 Shared Plugins" });
        Assert.Equal("01 Shared Plugins", folder.Folder);
        Assert.Equal(new[] { "P1", "P2" }, folder.Projects.Select(p => p.Name).Order());
        Assert.Equal("01 Shared Plugins/Sub", Assert.Single(folder.Folders).Path);

        var expanded = await client.Call<GetProjectsOutput>("get_projects", new { folder = "01 Shared Plugins", recursive = true });
        Assert.Equal(new[] { "S1" }, expanded.Folders[0].Projects!.Select(p => p.Name));
    }

    [Fact]
    public async Task A_missing_folder_fails_with_the_available_ones()
    {
        var client = session.Client;
        var error = await client.Fail("get_projects", new { folder = "Nope" });

        Assert.Contains("FOLDER_NOT_FOUND:", error);
        Assert.Contains("availableFolders", error);
        Assert.Contains("Libs", error);
    }

    [Fact]
    public async Task A_project_name_is_not_a_folder()
    {
        var client = session.Client;
        Assert.Contains("FOLDER_NOT_FOUND:", await client.Fail("get_projects", new { folder = "Root" }));
    }

    [Fact]
    public async Task Lists_every_configuration_pair_with_each_projects_mapping()
    {
        var client = session.Client;
        var result = await client.Call<GetSolutionConfigurationsOutput>("get_solution_configurations");

        Assert.Equal(6, result.SolutionConfigurations.Count);
        Assert.Single(result.SolutionConfigurations, c => c.IsActive);
        Assert.NotNull(result.ActiveConfiguration);
        Assert.All(result.SolutionConfigurations, pair => Assert.Equal(9, pair.Projects.Count));
        Assert.All(result.SolutionConfigurations.SelectMany(p => p.Projects), project => Assert.True(project.ShouldBuild));
    }

    [Fact]
    public async Task Maps_a_solution_platform_to_the_project_platform_it_names()
    {
        var client = session.Client;
        var result = await client.Call<GetSolutionConfigurationsOutput>("get_solution_configurations",
            new { solutionConfiguration = "Debug", platform = "x64", project = "A" });

        var pair = Assert.Single(result.SolutionConfigurations);
        Assert.Equal("x64", pair.SolutionPlatform);
        var mapping = Assert.Single(pair.Projects);
        Assert.Equal("Any CPU", mapping.ProjectPlatform);
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_fails_instead_of_falling_back()
    {
        var client = session.Client;

        var configuration = await client.Fail("get_solution_configurations", new { solutionConfiguration = "Nope" });
        Assert.Contains("CONFIGURATION_NOT_FOUND:", configuration);
        Assert.Contains("availableConfigurations", configuration);

        Assert.Contains("PROJECT_NOT_FOUND:", await client.Fail("get_solution_configurations", new { project = "Nope" }));
    }

    [Fact]
    public async Task Reports_direct_and_indirect_dependencies()
    {
        var client = session.Client;

        var c = Assert.Single((await client.Call<GetProjectDependenciesOutput>("get_project_dependencies", new { project = "C" })).Projects);
        Assert.Equal(new[] { "B" }, c.DirectDependencies.Select(d => d.Name));
        Assert.Equal(new[] { "B", "A" }, c.AllDependencies.Select(d => d.Name));

        var a = Assert.Single((await client.Call<GetProjectDependenciesOutput>("get_project_dependencies", new { project = "A" })).Projects);
        Assert.Empty(a.AllDependencies);
    }
}
