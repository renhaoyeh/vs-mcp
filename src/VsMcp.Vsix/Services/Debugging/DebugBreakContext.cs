using System;

namespace VsMcp.Services.Debugging
{
    // Tokens are valid only for one pause; even a step that returns to the same line invalidates them.
    internal sealed class DebugBreakContext
    {
        public string Id { get; private set; } = Guid.NewGuid().ToString("N");
        public void Invalidate() => Id = Guid.NewGuid().ToString("N");
        public void Require(string expected)
        {
            if (!string.Equals(Id, expected, StringComparison.Ordinal))
                throw new ArgumentException("Debugger context is stale. Read debugger_list_threads or debugger_get_callstack again.");
        }
    }
}
