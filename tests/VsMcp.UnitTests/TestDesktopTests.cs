using System.Diagnostics;
using VsMcp.IntegrationTests;
using Xunit;

namespace VsMcp.UnitTests;

public sealed class TestDesktopTests
{
    [Fact]
    public void Isolated_launch_preserves_unicode_environment_directory_and_exit_code()
    {
        using var desktop = new TestDesktop();
        Assert.False(desktop.WindowIsVisible(IntPtr.Zero));
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -Command \"if ($env:VSMCP_PROBE -ne '背景' -or $PWD.Path -ne $env:VSMCP_DIRECTORY) { exit 23 }; Start-Sleep -Milliseconds 500; exit 17\"")
        { WorkingDirectory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), UseShellExecute = false };
        info.Environment["VSMCP_PROBE"] = "背景";
        info.Environment["VSMCP_DIRECTORY"] = info.WorkingDirectory;
        using var process = desktop.Start(info);
        try
        {
            desktop.VerifyProcess(process.Id);
            Assert.True(process.WaitForExit(15000));
            Assert.Equal(17, process.ExitCode);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
