#nullable enable
namespace VsMcp.Contracts
{
    // A per-process discovery record, never an instruction to select an instance implicitly.
    internal sealed class InstanceRegistration
    {
        public int Pid { get; set; }
        public long ProcessStartTimeUtcTicks { get; set; }
        public int Port { get; set; }
        public string InstanceId { get; set; } = "";
    }
}
