using System;

namespace DriveBench.Transport;

/// <summary>Local processing times only. No payloads, credentials or raw network addresses.</summary>
internal sealed class InputDiagnostics
{
    private long _seen, _accepted;
    private string _rejection = "None";
    public void Observe(long now, bool accepted, string rejection)
    {
        _seen = now;
        if (accepted) _accepted = now;
        else _rejection = rejection;
    }
    public void ResetRejection() => _rejection = "None";
    public string Explain(string reason, long now)
    {
        string Gap(long at) => at == 0 ? "none" : Math.Clamp((now - at) / 1000, 0, 60_000).ToString()+"ms";
        // Fits the existing phone's 120-character captured-PC-reason limit for ordinary values.
        return $"{reason}; UDP {Gap(_seen)}, valid {Gap(_accepted)}, drop {_rejection}";
    }
}
