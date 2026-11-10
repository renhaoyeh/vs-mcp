using System;

namespace VsMcp.Services.Debugging
{
    internal sealed class LaunchTracking
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Status { get; private set; } = "unconfirmed";
        public bool IsPending => Status == "unconfirmed";

        public void Confirm() { if (IsPending) Status = "started"; }
        public void Fail() { if (IsPending) Status = "failed"; }
        public void RequireId(string requestId)
        {
            if (!string.Equals(requestId, Id, StringComparison.Ordinal))
                throw new ArgumentException("Request ID is stale or unknown. Read get_debug_status.withoutDebuggingRequest.");
        }
        public void Abandon(bool acknowledgeLaunchMayStillOccur)
        {
            if (!acknowledgeLaunchMayStillOccur)
                throw new ArgumentException("acknowledgeLaunchMayStillOccur must be true: abandoning tracking does not cancel the posted VS command or terminate a process.");
            if (IsPending) Status = "abandoned";
        }
    }
}
