using System.Text.Json;
using Xunit;

namespace VsMcp.IntegrationTests;

[Collection("Fixture")]
public sealed class BuildSettingsTests(FixtureSession session)
{
    [Fact]
    public async Task Reads_changes_and_restores_the_connected_IDE_preference()
    {
        var client = session.Client;
        var original = await client.Call<BuildSettingsOutput>("get_build_settings", new { });
        Assert.True(original.MaxParallelProjectBuilds > 0);
        Assert.Equal(BuildSettingsUpdateStatus.Read, original.UpdateStatus);
        Assert.Null(original.Previous);
        Assert.Null(original.PreviousMaxParallelProjectBuilds);
        var target = original.MaxParallelProjectBuilds == 1 ? 2 : 1;
        try
        {
            var changed = await client.Call<BuildSettingsOutput>("set_build_settings", new { maxParallelProjectBuilds = target });
            Assert.Equal(original.Instance.Pid, changed.Instance.Pid);
            Assert.Equal(original.MaxParallelProjectBuilds, changed.PreviousMaxParallelProjectBuilds);
            Assert.Equal(target, changed.MaxParallelProjectBuilds);
            Assert.Equal(BuildSettingsUpdateStatus.Applied, changed.UpdateStatus);
            Assert.Equal(original.MaxParallelProjectBuilds, changed.Previous!.MaxParallelProjectBuilds);
            // Independent automation read verifies the actual IDE preference, not only our contract.
            var dte = await client.Call<JsonElement>("execute_code", new { code = "return new { concurrentBuilds = System.Convert.ToInt32(DTE.Properties[\"Environment\", \"ProjectsAndSolution\"].Item(\"ConcurrentBuilds\").Value) };" });
            Assert.Equal(target, dte.GetProperty("result").GetProperty("concurrentBuilds").GetInt32());
            var read = await client.Call<BuildSettingsOutput>("get_build_settings", new { });
            Assert.Equal(target, read.MaxParallelProjectBuilds);
            var repeated = await client.Call<BuildSettingsOutput>("set_build_settings", new { maxParallelProjectBuilds = target });
            Assert.Equal(target, repeated.PreviousMaxParallelProjectBuilds);
            Assert.Equal(target, repeated.MaxParallelProjectBuilds);
            Assert.Equal(BuildSettingsUpdateStatus.Unchanged, repeated.UpdateStatus);
            Assert.Contains("INVALID_ARGUMENT", await client.Fail("set_build_settings", new { maxParallelProjectBuilds = 0 }));
            Assert.Equal(target, (await client.Call<BuildSettingsOutput>("get_build_settings", new { })).MaxParallelProjectBuilds);
        }
        finally
        {
            await client.Call<BuildSettingsOutput>("set_build_settings", new { maxParallelProjectBuilds = original.MaxParallelProjectBuilds });
            Assert.Equal(original.MaxParallelProjectBuilds, (await client.Call<BuildSettingsOutput>("get_build_settings", new { })).MaxParallelProjectBuilds);
        }
    }

    [Fact]
    public async Task Updates_all_supported_preferences_and_restores_the_snapshot()
    {
        var client = session.Client;
        var original = await client.Call<BuildSettingsOutput>("get_build_settings");
        var configuration = await client.Call<JsonElement>("get_solution_configurations");
        var startups = await client.Call<JsonElement>("startup_project_get");
        var fields = typeof(BuildSettingsValues).GetProperties();
        var restore = new Dictionary<string, object?>();
        var changes = new Dictionary<string, object?>();
        foreach (var field in fields)
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(field.Name);
            var value = field.GetValue(original);
            if (value == null)
            {
                Assert.Contains(name, original.UnsupportedSettings.Keys);
                continue;
            }
            Assert.DoesNotContain(name, original.UnsupportedSettings.Keys);
            restore[name] = JsonSerializer.SerializeToElement(value, Json.Options);
            var type = Nullable.GetUnderlyingType(field.PropertyType)!;
            object target = type == typeof(bool) ? !(bool)value
                : type == typeof(int) ? ((int)value == 1 ? 2 : 1)
                : Enum.GetValues(type).Cast<object>().First(v => !v.Equals(value));
            changes[name] = JsonSerializer.SerializeToElement(target, Json.Options);
        }
        // This host must expose the core settings; version/workload extras may be unavailable.
        Assert.Contains("lowPriorityBuild", changes.Keys);
        Assert.Contains("outputVerbosity", changes.Keys);
        try
        {
            var changed = await client.Call<BuildSettingsOutput>("set_build_settings", changes);
            Assert.Equal(BuildSettingsUpdateStatus.Applied, changed.UpdateStatus);
            Assert.Equal(original.Instance.Pid, changed.Instance.Pid);
            var read = await client.Call<BuildSettingsOutput>("get_build_settings");
            var actual = JsonSerializer.SerializeToElement(read, Json.Options);
            var previous = JsonSerializer.SerializeToElement(changed.Previous, Json.Options);
            foreach (var pair in changes)
            {
                Assert.Equal(((JsonElement)pair.Value!).GetRawText(), actual.GetProperty(pair.Key).GetRawText());
                Assert.Equal(((JsonElement)restore[pair.Key]!).GetRawText(), previous.GetProperty(pair.Key).GetRawText());
            }
            Assert.Equal(configuration.GetRawText(), (await client.Call<JsonElement>("get_solution_configurations")).GetRawText());
            Assert.Equal(startups.GetRawText(), (await client.Call<JsonElement>("startup_project_get")).GetRawText());
        }
        finally
        {
            var restored = await client.Call<BuildSettingsOutput>("set_build_settings", restore);
            Assert.NotEqual(BuildSettingsUpdateStatus.PendingApproval, restored.UpdateStatus);
            var read = JsonSerializer.SerializeToElement(await client.Call<BuildSettingsOutput>("get_build_settings"), Json.Options);
            foreach (var pair in restore)
                Assert.Equal(((JsonElement)pair.Value!).GetRawText(), read.GetProperty(pair.Key).GetRawText());
        }
    }

    [Fact]
    public async Task Rejects_empty_invalid_and_unsupported_patches_before_changing_other_fields()
    {
        var client = session.Client;
        var original = await client.Call<BuildSettingsOutput>("get_build_settings");
        Assert.Contains("INVALID_ARGUMENT", await client.Fail("set_build_settings", new { }));
        Assert.Contains("INVALID_ARGUMENT", await client.Fail("set_build_settings", new { maxParallelProjectBuilds = -1, lowPriorityBuild = !original.LowPriorityBuild }));
        await client.Fail("set_build_settings", new { outputVerbosity = "unknown", lowPriorityBuild = !original.LowPriorityBuild });
        foreach (var name in original.UnsupportedSettings.Keys)
        {
            var arguments = new Dictionary<string, object?> { ["maxParallelProjectBuilds"] = original.MaxParallelProjectBuilds == 1 ? 2 : 1 };
            var field = typeof(BuildSettingsValues).GetProperties().Single(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name) == name);
            var type = Nullable.GetUnderlyingType(field.PropertyType)!;
            arguments[name] = type == typeof(bool) ? true : type == typeof(int) ? 1 : "minimal";
            Assert.Contains("NOT_SUPPORTED", await client.Fail("set_build_settings", arguments));
        }
        var read = await client.Call<BuildSettingsOutput>("get_build_settings");
        Assert.Equal(original.MaxParallelProjectBuilds, read.MaxParallelProjectBuilds);
        Assert.Equal(original.LowPriorityBuild, read.LowPriorityBuild);
    }
}
