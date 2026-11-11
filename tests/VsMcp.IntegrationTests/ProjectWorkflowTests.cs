using System.Text;
using System.Text.Json;
using Xunit;

namespace VsMcp.IntegrationTests;

[Collection("Native")]
public sealed class NativeProjectWorkflowTests(NativeSession session)
{
    [Fact]
    public async Task Reads_native_cpp_configuration_and_reported_outputs_without_switching_configuration()
    {
        var before = await session.Client.Call<GetSolutionConfigurationsOutput>("get_solution_configurations");
        var configuration = await session.Client.Call<ProjectWorkflowOutput<CppConfigurationOutput>>("cpp_configuration_get", new { project = "CppApp", configuration = "Debug", platform = "x64" });
        Assert.True(configuration.Instance.Pid > 0);
        Assert.Equal("Debug", configuration.Data.Configuration);
        Assert.Equal("x64", configuration.Data.Platform);
        Assert.Equal("v145", configuration.Data.Properties["platformToolset"].Raw);
        Assert.Equal("v145", configuration.Data.Properties["platformToolset"].Evaluated);
        var mapped = await session.Client.Call<ProjectWorkflowOutput<CppConfigurationOutput>>("cpp_configuration_get", new { project = "CppApp" });
        Assert.False(string.IsNullOrWhiteSpace(mapped.Data.Configuration));
        Assert.False(string.IsNullOrWhiteSpace(mapped.Data.Platform));

        var items = await session.Client.Call<GetProjectItemsOutput>("project_items", new { project = "CppApp" });
        var source = Assert.Single(items.Items, item => item.Name == "app.cpp");
        var fileConfiguration = await session.Client.Call<ProjectWorkflowOutput<CppConfigurationOutput>>("cpp_configuration_get", new { project = "CppApp", configuration = "Debug", platform = "x64", file = source.FilePath });
        Assert.Equal(Path.GetFullPath(source.FilePath!), Path.GetFullPath(fileConfiguration.Data.File!));
        Assert.NotEqual("unavailable", fileConfiguration.Data.Properties["preprocessorDefinitions"].Source);

        var nativeBuild = await session.Client.Call<BuildOutput>("build_projects", new { projects = new[] { "NativeDebug" } });
        Assert.Equal(BuildStatus.Succeeded, nativeBuild.Status);
        var mapping = await session.Client.Call<GetSolutionConfigurationsOutput>("get_solution_configurations", new { project = "NativeDebug" });
        var activeSolution = Assert.Single(mapping.SolutionConfigurations, pair => pair.IsActive);
        var nativeMapping = Assert.Single(activeSolution.Projects);
        var outputs = await session.Client.Call<ProjectWorkflowOutput<ProjectOutputsOutput>>("project_outputs", new { project = "NativeDebug", configuration = nativeMapping.ProjectConfiguration, platform = nativeMapping.ProjectPlatform });
        Assert.Equal(configuration.Instance.Pid, outputs.Instance.Pid);
        Assert.EndsWith("NativeDebug.vcxproj", outputs.Data.Project, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".exe", outputs.Data.PrimaryOutput, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(outputs.Data.PrimaryOutput));
        Assert.EndsWith(".pdb", outputs.Data.ProgramDatabase, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(outputs.Data.ProgramDatabase));
        var after = await session.Client.Call<GetSolutionConfigurationsOutput>("get_solution_configurations");
        Assert.Equal(before.ActiveConfiguration?.SolutionConfiguration, after.ActiveConfiguration?.SolutionConfiguration);
        Assert.Equal(before.ActiveConfiguration?.SolutionPlatform, after.ActiveConfiguration?.SolutionPlatform);
    }

    [Fact]
    public async Task Per_file_properties_return_raw_macros_and_evaluated_values_and_restore_fixture()
    {
        var client = session.Client;
        var projectItems = await client.Call<GetProjectItemsOutput>("project_items", new { project = "CppApp" });
        var source = Assert.Single(projectItems.Items, item => item.Name == "app.cpp");
        const string rawValue = "$(ProjectDir)workflow-include";
        var sourcePath = Convert.ToBase64String(Encoding.UTF8.GetBytes(source.FilePath!));
        var mutate = $$"""
var targetPath = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String("{{sourcePath}}"));
var project = DTE.Solution.Projects.Cast<EnvDTE.Project>().Single(p => p.Name == "CppApp");
var native = (Microsoft.VisualStudio.VCProjectEngine.VCProject)project.Object;
var file = ((Microsoft.VisualStudio.VCProjectEngine.IVCCollection)native.Files).Cast<Microsoft.VisualStudio.VCProjectEngine.VCFile>().Single(f => System.String.Equals(f.FullPath, targetPath, System.StringComparison.OrdinalIgnoreCase));
var fileConfiguration = ((Microsoft.VisualStudio.VCProjectEngine.IVCCollection)file.FileConfigurations).Cast<Microsoft.VisualStudio.VCProjectEngine.VCFileConfiguration>().Single(c => ((Microsoft.VisualStudio.VCProjectEngine.VCConfiguration)c.ProjectConfiguration).ConfigurationName == "Debug" && ((Microsoft.VisualStudio.VCProjectEngine.VCPlatform)((Microsoft.VisualStudio.VCProjectEngine.VCConfiguration)c.ProjectConfiguration).Platform).Name == "x64");
var storage = (Microsoft.VisualStudio.VCProjectEngine.IVCRulePropertyStorage)fileConfiguration.Tool;
var previous = storage.GetUnevaluatedPropertyValue("AdditionalIncludeDirectories");
try {
    storage.SetPropertyValue("AdditionalIncludeDirectories", "$(ProjectDir)workflow-include");
    project.Save(project.FullName);
} catch {
    storage.SetPropertyValue("AdditionalIncludeDirectories", previous ?? "");
    project.Save(project.FullName);
    throw;
}
return previous;
""";
        var previousValue = (await client.Call<WorkflowCodeResult>("execute_code", new { code = mutate })).Result.GetString() ?? "";
        try
        {
            var read = await client.Call<ProjectWorkflowOutput<CppConfigurationOutput>>("cpp_configuration_get", new { project = "CppApp", configuration = "Debug", platform = "x64", file = source.FilePath });
            Assert.Equal(rawValue, read.Data.Properties["additionalIncludeDirectories"].Raw);
            Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source.FilePath!)!, "workflow-include")), Path.GetFullPath(read.Data.Properties["additionalIncludeDirectories"].Evaluated!));
        }
        finally
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(previousValue));
            var restore = $$"""
var targetPath = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String("{{sourcePath}}"));
var project = DTE.Solution.Projects.Cast<EnvDTE.Project>().Single(p => p.Name == "CppApp");
var native = (Microsoft.VisualStudio.VCProjectEngine.VCProject)project.Object;
var file = ((Microsoft.VisualStudio.VCProjectEngine.IVCCollection)native.Files).Cast<Microsoft.VisualStudio.VCProjectEngine.VCFile>().Single(f => System.String.Equals(f.FullPath, targetPath, System.StringComparison.OrdinalIgnoreCase));
var fileConfiguration = ((Microsoft.VisualStudio.VCProjectEngine.IVCCollection)file.FileConfigurations).Cast<Microsoft.VisualStudio.VCProjectEngine.VCFileConfiguration>().Single(c => ((Microsoft.VisualStudio.VCProjectEngine.VCConfiguration)c.ProjectConfiguration).ConfigurationName == "Debug" && ((Microsoft.VisualStudio.VCProjectEngine.VCPlatform)((Microsoft.VisualStudio.VCProjectEngine.VCConfiguration)c.ProjectConfiguration).Platform).Name == "x64");
var storage = (Microsoft.VisualStudio.VCProjectEngine.IVCRulePropertyStorage)fileConfiguration.Tool;
var previous = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String("{{encoded}}"));
storage.SetPropertyValue("AdditionalIncludeDirectories", previous);
project.Save(project.FullName);
return previous;
""";
            await client.Call<WorkflowCodeResult>("execute_code", new { code = restore });
        }
    }

    [Fact]
    public async Task Launch_setting_update_preserves_unspecified_fields_and_restores_previous_values()
    {
        var client = session.Client;
        var read = await client.Call<ProjectWorkflowOutput<CppLaunchSettingsOutput>>("cpp_launch_settings_get", new { project = "CppApp", configuration = "Debug", platform = "x64" });
        var previous = read.Data.Values;
        try
        {
            var changed = await client.Call<ProjectWorkflowOutput<CppLaunchSettingsOutput>>("cpp_launch_settings_set", new { project = "CppApp", configuration = "Debug", platform = "x64", arguments = "vs-mcp-workflow-check", debuggerType = "nativeOnly" });
            Assert.Equal(previous.Command, changed.Data.Values.Command);
            Assert.Equal("vs-mcp-workflow-check", changed.Data.Values.Arguments);
            Assert.Equal(previous.WorkingDirectory, changed.Data.Values.WorkingDirectory);
            Assert.Equal(previous.Environment, changed.Data.Values.Environment);
            Assert.Equal(previous.MergeEnvironment, changed.Data.Values.MergeEnvironment);
        Assert.Equal("nativeOnly", changed.Data.Values.DebuggerType);
        }
        finally
        {
            await client.Call<ProjectWorkflowOutput<CppLaunchSettingsOutput>>("cpp_launch_settings_set", new { project = "CppApp", configuration = "Debug", platform = "x64", arguments = previous.Arguments, debuggerType = previous.DebuggerType });
        }
    }

    [Fact]
    public async Task Existing_file_can_be_added_to_and_excluded_from_cpp_filter_without_deleting_it()
    {
        var client = session.Client;
        var file = Path.Combine(Path.GetTempPath(), "vs-mcp-project-item-" + Guid.NewGuid().ToString("N") + ".cpp");
        await File.WriteAllTextAsync(file, "// temporary project workflow fixture\n");
        var items = await client.Call<GetProjectItemsOutput>("project_items", new { project = "CppApp" });
        var originalSource = Assert.Single(items.Items, item => item.Name == "app.cpp");
        var itemPath = originalSource.Path;
        try
        {
            await client.Call<ProjectWorkflowOutput<ProjectItemMutationOutput>>("project_item_remove", new { project = "CppApp", itemPath });
            var added = await client.Call<ProjectWorkflowOutput<ProjectItemMutationOutput>>("project_item_add", new { project = "CppApp", filePath = file, filter = "Source Files" });
            itemPath = added.Data.Item;
            Assert.True(added.Data.Added);
            Assert.True(added.Data.FileStillExists);
            var removed = await client.Call<ProjectWorkflowOutput<ProjectItemMutationOutput>>("project_item_remove", new { project = "CppApp", itemPath });
            Assert.True(removed.Data.Removed);
            Assert.True(removed.Data.FileStillExists);
            Assert.True(File.Exists(file));
        }
        finally
        {
            try
            {
                var currentItems = await client.Call<GetProjectItemsOutput>("project_items", new { project = "CppApp" });
                var temporaryItem = currentItems.Items.FirstOrDefault(item => string.Equals(item.FilePath, file, StringComparison.OrdinalIgnoreCase));
                if (temporaryItem != null)
                    await client.Call<ProjectWorkflowOutput<ProjectItemMutationOutput>>("project_item_remove", new { project = "CppApp", itemPath = temporaryItem.Path });
                if (!currentItems.Items.Any(item => string.Equals(item.FilePath, originalSource.FilePath, StringComparison.OrdinalIgnoreCase)))
                    await client.Call<ProjectWorkflowOutput<ProjectItemMutationOutput>>("project_item_add", new { project = "CppApp", filePath = originalSource.FilePath, filter = "Source Files" });
            }
            finally { if (File.Exists(file)) File.Delete(file); }
        }
    }
}

internal sealed record WorkflowCodeResult(JsonElement Result);

[Collection("Fixture")]
public sealed class ManagedProjectWorkflowTests(FixtureSession session)
{
    [Fact]
    public async Task Reads_managed_outputs_from_visual_studio_output_groups()
    {
        var client = session.Client;
        var mapping = await client.Call<GetSolutionConfigurationsOutput>("get_solution_configurations", new { project = "A" });
        var activeSolution = Assert.Single(mapping.SolutionConfigurations, pair => pair.IsActive);
        var projectMapping = Assert.Single(activeSolution.Projects);
        var build = await client.Call<BuildOutput>("build_projects", new { projects = new[] { "A" } });
        Assert.Equal(BuildStatus.Succeeded, build.Status);

        var outputs = await client.Call<ProjectWorkflowOutput<ProjectOutputsOutput>>("project_outputs", new { project = "A", configuration = projectMapping.ProjectConfiguration, platform = projectMapping.ProjectPlatform });
        Assert.True(outputs.Data.OutputGroups.Any(group => group.CanonicalName == "Built" && group.Paths.Count > 0));
        Assert.EndsWith(".dll", outputs.Data.PrimaryOutput, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(outputs.Data.PrimaryOutput));
        Assert.EndsWith(".pdb", outputs.Data.ProgramDatabase, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(outputs.Data.ProgramDatabase));
    }
}
