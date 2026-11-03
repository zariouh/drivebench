namespace DriveBench.Transport;

/// <summary>Bounded, release-only output recovery. Times are monotonic microseconds.</summary>
public sealed class RecoveryPolicy
{
    private readonly object _gate = new();
    private long _unavailableSince, _healthySince;
    private int _attempts;
    private bool _busy, _stopped;
    public const long UnavailableUs = 1_000_000, StableUs = 30_000_000;
    public bool InFlight { get { lock (_gate) return _busy; } }
    public bool Exhausted { get { lock (_gate) return _attempts >= 2; } }
    public int Attempts { get { lock (_gate) return _attempts; } }
    public bool TryBegin(long now, bool armed, OutputAvailability output)
    {
        lock (_gate)
        {
            if (_stopped || _busy) return false;
            if (output.Ready && (armed || output.CanArm))
            {
                _unavailableSince = 0;
                if (_healthySince == 0) _healthySince = now;
                if (now - _healthySince >= StableUs) _attempts = 0;
                return false;
            }
            _healthySince = 0;
            // Never replace a controller that the receiver still permits to drive.
            if (armed) { _unavailableSince = 0; return false; }
            if (_unavailableSince == 0) _unavailableSince = now;
            if (now - _unavailableSince < UnavailableUs || _attempts >= 2) return false;
            _busy = true; _attempts++; return true;
        }
    }
    public bool TryBeginManual()
    {
        lock (_gate)
        {
            if (_stopped || _busy) return false;
            _busy = true; _attempts = 0; _healthySince = _unavailableSince = 0; return true;
        }
    }
    public void Complete()
    { lock (_gate) { _busy = false; _unavailableSince = _healthySince = 0; } }
    public void Stop() { lock (_gate) _stopped = true; }
}
