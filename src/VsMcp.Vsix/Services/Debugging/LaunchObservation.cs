using System;
using System.Collections.Generic;
using System.Linq;

namespace VsMcp.Services.Debugging
{
    internal sealed class LaunchProcess
    {
        public int Pid;
        public int ParentPid;
        public string Path;
        public DateTime Created;
    }

    internal static class LaunchObservation
    {
        public static List<LaunchProcess> Match(IEnumerable<LaunchProcess> snapshot, int host, DateTime requested, ISet<string> expected)
        {
            var processes = snapshot.ToDictionary(p => p.Pid);
            var found = new List<LaunchProcess>();
            foreach (var candidate in processes.Values.Where(p => p.Created >= requested && p.Path != null && expected.Contains(p.Path)))
            {
                var current = candidate;
                var seen = new HashSet<int>();
                while (seen.Add(current.Pid) && processes.TryGetValue(current.ParentPid, out var parent))
                {
                    // Reject missing timestamps and recycled ancestor PIDs.
                    if (parent.Created == default || parent.Created > current.Created) break;
                    if (parent.Pid == host) { found.Add(candidate); break; }
                    current = parent;
                }
            }
            return found;
        }
    }
}
