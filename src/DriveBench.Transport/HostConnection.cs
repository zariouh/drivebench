using System.Diagnostics;
using System.IO.Pipes;
using DriveBench.Core;

namespace DriveBench.Transport;

public sealed record OutputAvailability(bool Ready, bool CanArm, string Reason);

public interface IOutputOwner
{
    OutputAvailability Availability => new(Ready, CanArm, Ready ? "Waiting for the PC controller to confirm neutral" : "PC controller unavailable — click Restart controller on PC");
    bool Ready { get; }
    bool CanArm { get; }
    HostStatus Status { get; }
    ulong AppliedNetworkSequence { get; }
    void Publish(PadReport report, long absoluteDeadlineUs, ulong networkSequence);
    void Disarm();
}

/// <summary>Owns a separate output process. Its pump never extends the deadline of a received phone sample.</summary>
public sealed class HostConnection : IOutputOwner, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Stream? _pipe;
    private readonly SemaphoreSlim _changed = new(0, 1);
    private Process? _host;
    private Task[] _tasks = [];
    private bool _ready, _fault;
    private string _failureReason = "";
    private uint _epoch = 1;
    private ulong _sequence, _netSequence, _applied;
    private long _deadlineUs, _progress, _completed;
    private PadReport _report;
    private HostStatus _status;
    private readonly Dictionary<ulong, ulong> _mapping = new();
    public event Action<string>? Interrupted;
    public event Action<long>? SourceLeaseExpired;
    public OutputAvailability Availability
    {
        get { lock (_gate) return new(_ready && !_fault,
            _ready && !_fault && _status.Epoch == _epoch && _status.State == LeaseState.Idle,
            _fault ? _failureReason + " · Click Restart controller on PC" :
            !_ready ? "Starting the PC controller…" : "Waiting for the PC controller to confirm neutral"); }
    }
    public bool Ready { get { lock (_gate) return _ready && !_fault; } }
    public bool CanArm { get { lock (_gate) return _ready && !_fault && _status.Epoch == _epoch && _status.State == LeaseState.Idle; } }
    public HostStatus Status { get { lock (_gate) return _status; } }
    public ulong AppliedNetworkSequence { get { lock (_gate) return _applied; } }
    public async Task StartAsync(string executable, bool real, CancellationToken token, string? managedAssembly = null)
    {
        string name = "DriveBench-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (managedAssembly != null) start.ArgumentList.Add(managedAssembly);
        start.ArgumentList.Add(name); start.ArgumentList.Add(real ? "vigem" : "dry-run");
        _host = Process.Start(start) ?? throw new IOException("Could not start output host");
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(10_000, token); }
        catch { pipe.Dispose(); throw; }
        await AttachAsync(pipe, token);
    }
    // The same pump/status/watchdog can be exercised over a test duplex stream without a virtual driver.
    internal async Task AttachAsync(Stream stream, CancellationToken token)
    {
        if (_pipe != null) throw new InvalidOperationException("Output already attached");
        _pipe = stream;
        _progress = Stopwatch.GetTimestamp();
        _tasks = [Task.Run(ReadStatus), Task.Run(Pump), Task.Run(Supervise)];
        long until = MonoClock.Us + 10_000_000;
        while (!Ready)
        {
            lock (_gate) { if (_fault) throw new IOException("Output host could not start. Check host.log."); }
            if (MonoClock.Us >= until) { Fail("Output host startup timed out"); throw new IOException("Output host startup timed out"); }
            await Task.Delay(10, token);
        }
    }
    public void Disarm()
    {
        lock (_gate)
        {
            if (_epoch == uint.MaxValue) { _fault = true; return; }
            _epoch++; _report = default; _deadlineUs = 0; _netSequence = _applied = 0; _mapping.Clear();
        }
        Wake();
    }
    public void Publish(PadReport report, long absoluteDeadlineUs, ulong networkSequence)
    {
        lock (_gate)
        {
            if (!_ready || _fault || absoluteDeadlineUs <= MonoClock.Us || _status.Epoch!=_epoch || _status.State is not (LeaseState.Idle or LeaseState.Armed)) return;
            _report = report; _deadlineUs = absoluteDeadlineUs; _netSequence = networkSequence;
        }
        Wake();
    }
    private void Wake()
    {
        // The mailbox is the state under _gate, not a queue of old reports. Coalesce wakeups.
        if (_changed.CurrentCount != 0) return;
        try { _changed.Release(); } catch (SemaphoreFullException) { }
    }
    private async Task Pump()
    {
        try
        {
            ulong sentNetwork = 0; uint sentEpoch = 0; long nextNeutralUs = 0;
            while (!_stop.IsCancellationRequested)
            {
                InputFrame? frame = null; long expiredDeadline=0; long waitUs;
                lock (_gate)
                {
                    long now = Stopwatch.GetTimestamp();
                    long nowUs = MonoClock.Us;
                    if (_deadlineUs != 0 && nowUs >= _deadlineUs)
                    {
                        expiredDeadline = _deadlineUs;
                        _epoch++; _deadlineUs = 0; _netSequence = 0; _report = default; _applied = 0; _mapping.Clear();
                    }
                    bool armed = _ready && !_fault && _deadlineUs != 0;
                    if (sentEpoch != _epoch || (armed ? sentNetwork != _netSequence : nowUs >= nextNeutralUs))
                    {
                        long deadline = armed ? Math.Min(MonoClock.Ticks(_deadlineUs), now + Stopwatch.Frequency / 10) : now + Stopwatch.Frequency / 10;
                        frame = new(++_sequence, now, deadline, _epoch, armed, armed ? _report : default);
                        sentEpoch = _epoch; sentNetwork = _netSequence; nextNeutralUs = nowUs + 16_000;
                        if (armed) _mapping[_sequence] = _netSequence;
                        if (_mapping.Count > 128) _mapping.Remove(_mapping.Keys.Min());
                    }
                    // While armed, only NEW input can renew the source deadline. No timer resends.
                    waitUs = Math.Max(1, (armed ? _deadlineUs : nextNeutralUs) - nowUs);
                }
                if (expiredDeadline != 0) SourceLeaseExpired?.Invoke(expiredDeadline);
                if (frame is { } value)
                {
                    await _pipe!.WriteAsync(value.Encode(), _stop.Token);
                    continue; // Re-check time after a possibly delayed pipe write, before waiting.
                }
                await _changed.WaitAsync(TimeSpan.FromMilliseconds(Math.Ceiling(waitUs / 1000d)), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!_stop.IsCancellationRequested) Fail("Output connection ended"); }
    }
    private async Task ReadStatus()
    {
        byte[] bytes = new byte[HostStatus.Size];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _pipe!.ReadExactlyAsync(bytes, _stop.Token);
                var status = HostStatus.Decode(bytes); bool expired = false;long expiredDeadline=0;
                lock (_gate)
                {
                    if (status.CompletedTicks <= _completed) continue;
                    _completed = _progress = status.CompletedTicks; _status = status; _ready = true;
                    if (_mapping.TryGetValue(status.AppliedSequence, out var seq)) _applied = Math.Max(_applied, seq);
                    expired = status.State == LeaseState.Expired && status.Epoch == _epoch;
                    if(expired) expiredDeadline=_deadlineUs;
                }
                if (expired) { Disarm(); SourceLeaseExpired?.Invoke(expiredDeadline); }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        { if (!_stop.IsCancellationRequested) Fail("Output host disconnected; reopen receiver"); }
    }
    private async Task Supervise()
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                bool ready; long progress; lock (_gate) { ready = _ready; progress = _progress; }
                long now = Stopwatch.GetTimestamp();
                if (_host is { HasExited: true }) { Fail("Output host exited; reopen receiver. Check host.log."); return; }
                if ((!ready && now - start > Stopwatch.Frequency * 10) || (ready && now - progress > Stopwatch.Frequency * .150))
                { Fail("Output host stopped progressing; stopped its process"); return; }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Fail(string reason)
    {
        lock (_gate) { if (_fault) return; _fault = true; _failureReason = reason; _ready = false; _report = default; }
        _stop.Cancel(); try { _pipe?.Dispose(); } catch { }
        try { if (_host is { HasExited: false }) _host.Kill(entireProcessTree: true); } catch { }
        Interrupted?.Invoke(reason);
    }
    public async ValueTask DisposeAsync()
    {
        Disarm(); _stop.Cancel(); _pipe?.Dispose();
        try { await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        try { if (_host is { HasExited: false } && !_host.WaitForExit(250)) _host.Kill(entireProcessTree: true); } catch { }
        _host?.Dispose(); _stop.Dispose();
    }
}