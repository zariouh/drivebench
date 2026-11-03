using DriveBench.Core;

namespace DriveBench.Transport;

/// <summary>Stable output owner while a released controller process is replaced.</summary>
public sealed class OutputSlot : IOutputOwner
{
    private readonly object _gate = new();
    private IOutputOwner? _current;
    private string _reason = "PC controller unavailable — click Restart controller on PC";
    public void Replace(IOutputOwner? next, string reason = "Restarting the PC controller…")
    {
        lock (_gate)
        {
            _current?.Disarm();
            next?.Disarm(); // Replacement always starts a fresh neutral handshake.
            _current = next; _reason = reason;
        }
    }
    public OutputAvailability Availability { get { lock (_gate) return _current?.Availability ?? new(false,false,_reason); } }
    public bool Ready => Availability.Ready;
    public bool CanArm => Availability.CanArm;
    public HostStatus Status { get { lock (_gate) return _current?.Status ?? default; } }
    public ulong AppliedNetworkSequence { get { lock (_gate) return _current?.AppliedNetworkSequence ?? 0; } }
    public void Publish(PadReport report, long absoluteDeadlineUs, ulong networkSequence)
    { lock (_gate) _current?.Publish(report,absoluteDeadlineUs,networkSequence); }
    public void Disarm() { lock (_gate) _current?.Disarm(); }
}
