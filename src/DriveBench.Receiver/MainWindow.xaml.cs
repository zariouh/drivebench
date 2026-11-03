using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DriveBench.Transport;
using QRCoder;

namespace DriveBench.Receiver;
public partial class MainWindow : Window
{
    private sealed record Adapter(IPAddress Address, string Name) { public override string ToString() => $"{Name} — {Address}"; }
    private Task _timingWrite = Task.CompletedTask;
    private long _savedReceiveFault = -1;
    private readonly string _timingPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DriveBench-receiver-timing.log");
    private HostConnection? _host;
    private readonly OutputSlot _output = new();
    private readonly RecoveryPolicy _recovery = new();
    private readonly SemaphoreSlim _controllerGate = new(1,1);
    private RemoteReceiver? _receiver;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private IPAddress? _address;
    private string? _pairText;
    private long _qrUntil;
    private bool _closing, _closed;
    public MainWindow()
    {
        InitializeComponent();
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && Private(a.Address))
                .Select(a => new Adapter(a.Address, n.Name))).ToArray();
        Adapters.ItemsSource = adapters; if (adapters.Length > 0) Adapters.SelectedIndex = 0;
        else Status.Text = "Connect the PC to a local Wi-Fi network, then reopen this receiver.";
        _timer.Tick += (_, _) => Refresh();
    }
    private static bool Private(IPAddress address)
    { byte[] b = address.GetAddressBytes(); return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168); }
    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_receiver != null || Adapters.SelectedItem is not Adapter adapter) return;
        var oldApps = Process.GetProcessesByName("DriveBench.App");
        bool oldOpen = oldApps.Length > 0; foreach (var app in oldApps) app.Dispose();
        if (oldOpen) { Status.Text = "Close the old DriveBench PC test application, then click Start receiver again."; return; }
        StartButton.IsEnabled = Adapters.IsEnabled = false; Status.Text = "Starting Wi-Fi receiver…";
        try
        {
            _address = adapter.Address;
            string executable = Path.Combine(AppContext.BaseDirectory, "DriveBench.Host.exe");
            if (!File.Exists(executable)) throw new IOException("Extract the complete Wi-Fi receiver ZIP into its own folder first.");
            _recovery.TryBeginManual(); // Serialize the initial start with later recovery polls.
            _receiver = new RemoteReceiver(_output, Approve);
            _receiver.Start(_address); MakeQr(); _timer.Start();
            NewQrButton.IsEnabled = CopyButton.IsEnabled = true;
            try { await ReplaceController(); }
            catch (OperationCanceledException) when (_closing) { }
            catch (Exception) { /* Keep pairing available; the bounded recovery policy retries output startup. */ }
            finally
            {
                _recovery.Complete();
                if (!_closing) { RestartButton.IsEnabled = true; Refresh(); }
            }
        }
        catch (Exception ex) { Status.Text = "Could not start: " + ex.Message + " Close this receiver and retry. Ports: UDP 49173, TCP 49174."; }
    }
    private async Task ReplaceController(bool automatic = false)
    {
        await _controllerGate.WaitAsync(_lifetime.Token);
        try
        {
            if (_receiver != null)
            {
                if (!_receiver.TryPrepareOutputRecovery(() => _output.Replace(null),allowArmed:!automatic)) return;
            }
            else _output.Replace(null);
            if (_host != null) { await _host.DisposeAsync(); _host = null; }
            _lifetime.Token.ThrowIfCancellationRequested();
            var host = new HostConnection(); _host = host;
            host.Interrupted += reason => { if (ReferenceEquals(_host,host)) _receiver?.StopInput(reason,includeTiming:true); };
            host.SourceLeaseExpired += deadline => { if (ReferenceEquals(_host, host)) _receiver?.OutputSourceExpired(deadline); };
            await host.StartAsync(Path.Combine(AppContext.BaseDirectory,"DriveBench.Host.exe"),true,_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            _output.Replace(host);
            long until = MonoClock.Us + 2_000_000;
            // A valid Resume can win after neutral acknowledgement but before this UI continuation.
            while (!host.CanArm && _receiver?.View.Armed != true)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (!host.Ready || MonoClock.Us >= until) throw new IOException(host.Availability.Reason);
                await Task.Delay(20,_lifetime.Token);
            }
        }
        catch (Exception ex)
        {
            _output.Replace(null,"PC controller could not restart: " + ex.Message + " · Click Restart controller on PC");
            throw;
        }
        finally { _controllerGate.Release(); }
    }
    private async void OnRestartController(object sender, RoutedEventArgs e)
    {
        if (_closing || !_recovery.TryBeginManual()) return;
        RestartButton.IsEnabled = false;
        try { await ReplaceController(); }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { Status.Text = "Controller restart failed: " + ex.Message; }
        finally { _recovery.Complete(); if (!_closing) { RestartButton.IsEnabled = true; Refresh(); } }
    }
    private async Task RecoverAutomatically()
    {
        RestartButton.IsEnabled = false;
        try { await ReplaceController(automatic:true); }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception) { /* Availability retains the failure for both screens; retries are bounded. */ }
        finally
        {
            _recovery.Complete();
            if (!_closing) { RestartButton.IsEnabled = true; Refresh(); }
        }
    }
    private Task<bool> Approve(string name, IPAddress address) => Dispatcher.InvokeAsync(() =>
        !_closing && MessageBox.Show(this, $"Pair {name} at {address}?\n\nApprove only the phone that just scanned your QR.", "DriveBench phone pairing", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes).Task;
    private void MakeQr()
    {
        if (_receiver == null || _address == null) return;
        _pairText = _receiver.OpenPairing(_address); _qrUntil = MonoClock.Us + 60_000_000;
        using var data = QRCodeGenerator.GenerateQrCode(_pairText, QRCodeGenerator.ECCLevel.Q);
        using var qr = new PngByteQRCode(data);
        using var stream = new MemoryStream(qr.GetGraphic(6));
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        PairQr.Source = bitmap;
    }
    private void OnNewQr(object sender, RoutedEventArgs e) => MakeQr();
    private void OnCopy(object sender, RoutedEventArgs e) { if (_pairText != null && MonoClock.Us < _qrUntil) Clipboard.SetText(_pairText); }
    private void OnStop(object sender, RoutedEventArgs e) => _receiver?.StopInput("Stopped on PC — tap Resume on the phone");
    private void OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { OnStop(sender, e); e.Handled = true; } }
    private void Refresh()
    {
        if (_receiver == null) return;
        if(_timingWrite.IsCompleted && _savedReceiveFault != _receiver.ReceiveFaultNumber)
        {
            _savedReceiveFault = _receiver.ReceiveFaultNumber;
            _timingWrite = SaveReceiveTiming(_receiver.ReceiveTimingReport);
        }
        var view = _receiver.View; var output = _output.Status;
        var availability = _output.Availability;
        Status.Text = view.Message;
        Output.Text = $"Steering {output.Report.Steering / 327.67:+0;-0;0}% · Brake {output.Report.Brake * 100 / 255}% · Throttle {output.Report.Throttle * 100 / 255}%";
        Details.Text = view.Connected ? $"{(view.Recovering ? "Recovering · neutral" : view.Armed ? "Driving" : "Released")} · {(view.Sync ? "Clock ready" : "Synchronizing")}\nReceived {view.ReceivedSequence} · Applied {view.AppliedSequence}" : $"PC address: {_address}\nUDP {_receiver.InputPort} · TLS {_receiver.ControlPort}";
        RecoveryStatus.Text = _recovery.InFlight ? "Restoring PC controller · controls released" :
            !availability.CanArm && !view.Armed && _recovery.Exhausted ? "Automatic recovery stopped after two attempts · use Restart controller to retry" :
            _recovery.Attempts > 0 ? "Controller recovery complete · tap Resume on the phone when ready" : "";
        if (!_closing && _recovery.TryBegin(MonoClock.Us,view.Armed || view.Recovering,availability)) _ = RecoverAutomatically();
        if (view.Connected || MonoClock.Us >= _qrUntil)
        {
            PairQr.Source = null; _pairText = null; CopyButton.IsEnabled = false;
            QrCaption.Text = view.Connected ? (view.Recovering ? "Recovering automatically from fresh phone controls." : _output.CanArm || view.Armed ? "Phone paired. Tap Resume on the phone to drive." : _output.Availability.Reason) : "QR expired. Click New pairing QR.";
        }
        else { CopyButton.IsEnabled = true; QrCaption.Text = $"Scan within {Math.Max(0, (_qrUntil - MonoClock.Us) / 1_000_000)} seconds."; }
    }
    private Task SaveReceiveTiming(string report) => Task.Run(() =>
    {
        try { File.WriteAllText(_timingPath, report); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine("Receive timing report unavailable: " + ex.Message); }
    });
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closed) return; e.Cancel = true; if (_closing) return;
        _closing = true; _recovery.Stop(); _timer.Stop(); _lifetime.Cancel();
        if (_receiver != null)
        {
            await _receiver.DisposeAsync();
            await _timingWrite;
            await SaveReceiveTiming(_receiver.ReceiveTimingReport);
        }
        await _controllerGate.WaitAsync();
        try { _output.Replace(null); if (_host != null) await _host.DisposeAsync(); }
        finally { _controllerGate.Release(); }
        _closed = true; Close();
    }
}
