using VsMcp.Services.Debugging;
using Xunit;

namespace VsMcp.UnitTests;

public sealed class LaunchObservationTests
{
    [Fact]
    public void Confirms_only_new_matching_descendants_and_rejects_recycled_parents()
    {
        var requested = DateTime.UtcNow;
        LaunchProcess P(int id, int parent, int seconds, string path = null) => new() { Pid = id, ParentPid = parent, Created = requested.AddSeconds(seconds), Path = path };
        var snapshot = new[] {
            P(1, 0, -100), P(2, 1, 0), P(3, 2, 1, "app.exe"),
            P(4, 1, -1, "app.exe"), // Already running before the request.
            P(5, 0, 1, "app.exe"), // Another instance/user launched the same path.
            P(6, 1, 1, "other.exe"),
            P(7, 1, 3), P(8, 7, 2, "app.exe"), // Parent PID was reused.
            P(9, 10, 1, "app.exe"), P(10, 9, 1), // Cycle cannot prove ancestry.
            P(11, 99, 1, "app.exe"), // Missing parent is inconclusive.
        };
        var found = LaunchObservation.Match(snapshot, 1, requested, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "APP.EXE" });
        Assert.Equal(3, Assert.Single(found).Pid);
        Assert.Empty(LaunchObservation.Match(snapshot, 1, requested, new HashSet<string> { "missing.exe" }));
    }
}
