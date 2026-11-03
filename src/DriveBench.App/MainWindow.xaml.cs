using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DriveBench.Core;

namespace DriveBench.App;
public partial class MainWindow : Window
{
    private NamedPipeClientStream? _pipe;
    private Process? _host;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _sendTimer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private int _writeBusy, _faultPending;
    private long _lastHostProgress, _lastCompletedTick;
    private volatile bool _ready, _holding;
    private bool _closing;
    private long _runUntilTicks;
    private ulong _sequence;
    private uint _epoch = 1;
    private HostStatus _latest;
    private readonly object _statusGate = new();
    public MainWindow()
    {
        InitializeComponent();
        _sendTimer.Tick += async (_, _) => await Tick();
        PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) { StopInput(); e.Handled = true; } };
    }
    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_host != null) return;
        bool real = Backend.SelectedIndex == 1;
        if (real && MessageBox.Show("This optional test uses the retired ViGEmBus prototype backend. It requires an existing compatible, signed driver. No driver will be installed or security setting changed. Continue?", "Experimental driver test", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        string hostPath = Path.Combine(AppContext.BaseDirectory, "DriveBench.Host.exe");
        if (!File.Exists(hostPath)) { Status.Text = "Host is missing. Extract the COMPLETE Windows package first."; return; }
        StartButton.IsEnabled = Backend.IsEnabled = false;
        try
        {
            string name = "DriveBench-" + Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(hostPath) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(name); start.ArgumentList.Add(real ? "vigem" : "dry-run");
            _host = Process.Start(start) ?? throw new IOException("Could not launch host");
            _pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Status.Text = "Starting host…";
            await _pipe.ConnectAsync(10_000, _lifetime.Token);
            _lastHostProgress = Stopwatch.GetTimestamp();
            _ = Task.Run(ReadStatus);
            _ = Task.Run(Supervise);
            _sendTimer.Start();
        }
        catch (Exception ex) { Fault("Start failed: " + ex.Message + " Check %LOCALAPPDATA%\\DriveBench\\host.log"); }
    }
    private async Task ReadStatus()
    {
        byte[] bytes = new byte[HostStatus.Size];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await _pipe!.ReadExactlyAsync(bytes, _lifetime.Token);
                var status = HostStatus.Decode(bytes);
                if (status.CompletedTicks <= Interlocked.Read(ref _lastCompletedTick)) continue;
                Interlocked.Exchange(ref _lastCompletedTick, status.CompletedTicks);
                // Check the actual report progress timestamp, not merely a newly delivered pipe message.
                Interlocked.Exchange(ref _lastHostProgress, status.CompletedTicks);
                lock (_statusGate) _latest = status;
                _ready = true;
                if (status.State == LeaseState.Expired && status.Epoch == Volatile.Read(ref _epoch)) Interlocked.Exchange(ref _faultPending, 1);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
        { if (!_closing) Fault("Host disconnected. " + ex.Message + " Check the host log."); }
    }
    private async Task Supervise()
    {
        long startup = Stopwatch.GetTimestamp();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                long now = Stopwatch.GetTimestamp();
                if (_host is { HasExited: true }) { Fault("Host exited. Check %LOCALAPPDATA%\\DriveBench\\host.log"); return; }
                if (!_ready && (now - startup) / (double)Stopwatch.Frequency > 10) { Fault("Host startup timed out."); return; }
                if (_ready && (now - Interlocked.Read(ref _lastHostProgress)) / (double)Stopwatch.Frequency > .150)
                { Fault("Output host stopped progressing; it was terminated. Restart the app to retest."); return; }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task Tick()
    {
        if (_closing) return;
        if (Interlocked.Exchange(ref _faultPending, 0) != 0) { StopInput(); Status.Text = "Input expired: released. Hold again deliberately to resume."; }
        if (_runUntilTicks != 0 && Stopwatch.GetTimestamp() >= _runUntilTicks) StopInput();
        HoldButton.IsEnabled = TimedButton.IsEnabled = _ready;
        if (_ready)
        {
            HostStatus s; lock (_statusGate) s = _latest;
            if (s.State != LeaseState.Expired)
                Status.Text = s.RealDriver ? "Virtual Xbox device attached. " + (s.ReadbackMatches ? "XInput readback matches." : "XInput readback not yet matched.") : "DRY RUN — no virtual device and no game input.";
            Output.Text = $"Host submitted: steer {s.Report.Steering}, brake {s.Report.Brake}/255, throttle {s.Report.Throttle}/255 · {s.State}";
        }
        if (_pipe is not { IsConnected: true } || Interlocked.Exchange(ref _writeBusy, 1) != 0) return;
        try
        {
            long now = Stopwatch.GetTimestamp();
            var report = _holding && _ready ? PadReport.FromPercent(Steering.Value, Brake.Value, Throttle.Value) : default;
            var frame = new InputFrame(++_sequence, now, now + Stopwatch.Frequency / 10, _epoch, _holding && _ready, report);
            await _pipe.WriteAsync(frame.Encode(), _lifetime.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { if (!_closing) Fault("Local connection failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _writeBusy, 0); }
    }
    private void Fault(string text)
    {
        _ready = _holding = false;
        try { if (_host is { HasExited: false }) _host.Kill(entireProcessTree: true); } catch { }
        try { _pipe?.Dispose(); } catch { }
        Dispatcher.BeginInvoke(() => { _sendTimer.Stop(); HoldButton.IsEnabled = TimedButton.IsEnabled = false; ClearSliders(); Status.Text = text; });
    }
    private void ClearSliders() { Steering.Value = Brake.Value = Throttle.Value = 0; }
    private void StopInput()
    {
        _holding = false;
        _runUntilTicks = 0;
        if (_epoch == uint.MaxValue) { Fault("Epoch exhausted; restart."); return; }
        _epoch++; ClearSliders();
        if (HoldButton.IsMouseCaptured) HoldButton.ReleaseMouseCapture();
    }
    private void OnHoldDown(object sender, MouseButtonEventArgs e) { if (_ready) { _holding = true; HoldButton.CaptureMouse(); } e.Handled = true; }
    private void OnHoldUp(object sender, MouseButtonEventArgs e) { StopInput(); e.Handled = true; }
    private void OnLostCapture(object sender, MouseEventArgs e) { if (_holding) StopInput(); }
    private void OnStop(object sender, RoutedEventArgs e) => StopInput();
    private void OnTimedTest(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _runUntilTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        _holding = true;
    }
    private void OnDeactivated(object? sender, EventArgs e) { if (Stopwatch.GetTimestamp() >= _runUntilTicks) StopInput(); }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { StopInput(); e.Handled = true; }
        if (e.Key == Key.Space) { if (_ready && !e.IsRepeat) _holding = true; e.Handled = true; }
    }
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true; _holding = _ready = false; _sendTimer.Stop(); _lifetime.Cancel();
        _pipe?.Dispose(); // Host sees EOF and submits neutral before disposing its device.
        try { if (_host is { HasExited: false } && !_host.WaitForExit(250)) _host.Kill(entireProcessTree: true); } catch { }
    }
}
