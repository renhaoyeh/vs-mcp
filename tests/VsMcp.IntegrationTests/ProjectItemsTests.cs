using Xunit;

namespace VsMcp.IntegrationTests;

[Collection("Fixture")]
public sealed class ProjectItemsTests(FixtureSession session)
{
    [Fact]
    public async Task Project_items_lists_csharp_files_with_physical_paths()
    {
        var result = await session.Client.Call<GetProjectItemsOutput>("project_items", new { project = "A" });
        Assert.True(result.Instance.Pid > 0);
        Assert.EndsWith("A\\A.csproj", result.Project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Items, item => item.Name == "Class1.cs" && item.Kind == "file" && item.FilePath!.EndsWith(Path.Combine("A", "Class1.cs"), StringComparison.OrdinalIgnoreCase));
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Requires_an_exact_unambiguous_project()
    {
        Assert.Contains("PROJECT_NOT_FOUND:", await session.Client.Fail("project_items", new { project = "missing" }));
        Assert.Contains("PROJECT_NOT_FOUND:", await session.Client.Fail("project_items", new { project = (string?)null }));
    }
}

[Collection("Native")]
public sealed class NativeProjectItemsTests(NativeSession session)
{
    [Fact]
    public async Task Project_items_lists_cpp_source_with_physical_path()
    {
        var result = await session.Client.Call<GetProjectItemsOutput>("project_items", new { project = "CppApp" });
        Assert.EndsWith("CppApp\\CppApp.vcxproj", result.Project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Items, item => item.Name == "Source Files" && item.Kind == "folder");
        Assert.Contains(result.Items, item => item.Name == "app.cpp" && item.Kind == "file" && item.FilePath!.EndsWith(Path.Combine("CppApp", "app.cpp"), StringComparison.OrdinalIgnoreCase));
    }
}
