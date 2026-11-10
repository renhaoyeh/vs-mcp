using System.Text.Json;
using System.Runtime.InteropServices;
using Xunit;

namespace VsMcp.IntegrationTests;

/// <summary>Advanced code execution and the intentionally small agent-facing surface.</summary>
[Collection("Fixture")]
public sealed class CodeToolsTests(FixtureSession session)
{
    private sealed record CodeResult(JsonElement Result);


    [Fact]
    public async Task Runs_a_method_body_on_the_ui_thread_with_the_dte()
    {
        var client = session.Client;
        var result = await client.Call<CodeResult>("execute_code", new { code = "return new { version = DTE.Version, sameThread = Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess() };" });

        Assert.Equal("18.0", result.Result.GetProperty("version").GetString());
        Assert.True(result.Result.GetProperty("sameThread").GetBoolean());
    }

    [Fact]
    public async Task Gives_the_snippet_the_solution_and_the_service_provider()
    {
        var client = session.Client;
        var result = await client.Call<CodeResult>("execute_code", new { code = "return new { solution = Solution != null, services = Services != null };" });

        Assert.True(result.Result.GetProperty("solution").GetBoolean());
        Assert.True(result.Result.GetProperty("services").GetBoolean());
    }

    [Fact]
    public async Task Reports_a_compile_error_with_its_location()
    {
        var client = session.Client;
        var error = await client.Fail("execute_code", new { code = "return nope;" });

        Assert.Contains("COMPILE_ERROR:", error);
        Assert.Contains("CS0103", error);
        Assert.Contains("snippet.cs(1,", error);
    }

    [Fact]
    public async Task Reports_an_exception_thrown_by_the_snippet()
    {
        var client = session.Client;
        var error = await client.Fail("execute_code", new { code = "throw new System.InvalidOperationException(\"boom\");" });

        Assert.Contains("EXECUTION_ERROR:", error);
        Assert.Contains("boom", error);
    }

    [Fact]
    public async Task Shows_and_hides_only_the_connected_main_window()
    {
        var client = session.Client;
        var before = await client.Call<CodeResult>("execute_code", new { code = "return DTE.MainWindow.HWnd.ToInt64();" });
        var handle = new IntPtr(before.Result.GetInt64());
        var visible = IsWindowVisible(handle);
        var identity = await client.Call<InstanceInfo>("get_vs_info");
        try
        {
            Assert.Contains("INVALID_ARGUMENT:", await client.Fail("show_vs_window", new { visible = false, activate = true }));
            Assert.Equal(visible, IsWindowVisible(handle));
            var hidden = await client.Call<JsonElement>("show_vs_window", new { visible = false });
            Assert.False(hidden.GetProperty("visible").GetBoolean());
            Assert.False(hidden.GetProperty("activationRequested").GetBoolean());
            Assert.Equal(identity.Pid, hidden.GetProperty("instance").GetProperty("pid").GetInt32());
            Assert.False(IsWindowVisible(handle));
            var responsive = await client.Call<CodeResult>("execute_code", new { code = "return Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess();" });
            Assert.True(responsive.Result.GetBoolean());
            var shown = await client.Call<JsonElement>("show_vs_window");
            Assert.True(shown.GetProperty("visible").GetBoolean());
            Assert.True(IsWindowVisible(handle));
            Assert.False(shown.GetProperty("activationRequested").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(shown.GetProperty("title").GetString()));
        }
        finally { await client.Call<JsonElement>("show_vs_window", new { visible }); }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [Fact]
    public async Task Does_not_expose_other_window_or_terminal_display_helpers()
    {
        var names = (await session.Client.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Assert.Contains("show_vs_window", names);
        foreach (var name in new[] { "window_activate", "window_list", "terminal_show", "toolwindow_show", "test_run_all", "test_debug" }) Assert.DoesNotContain(name, names);
    }
}
