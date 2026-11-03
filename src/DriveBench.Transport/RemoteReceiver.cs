#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace DriveBench.Transport;

public sealed record ReceiverView(string Message, bool Connected, bool Armed, bool Sync, uint Epoch,
    double UncertaintyMs, ulong ReceivedSequence, ulong AppliedSequence, bool Recovering = false);

public sealed class RemoteReceiver : IAsyncDisposable
{
    private sealed class Session(CancellationToken token)
    {
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        public readonly Admission Input = new();
        public readonly InputDiagnostics Diagnostics = new();
        public readonly SemaphoreSlim Changed = new(0,1);
        public bool Retired;
        public ulong Id = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)) & 0x7fff_ffff_ffff_ffff;
        public readonly byte[] InKey = RandomNumberGenerator.GetBytes(32), OutKey = RandomNumberGenerator.GetBytes(32);
        public required IPAddress Address;
        public IPEndPoint? Endpoint;
        public int Samples;
        public long LastCommand, LastStatusUs;
        public ulong StatusSequence;
    }
    private sealed record Feedback(ulong Session,ulong Sequence,ushort Flags,ulong InputSequence,long InputSentUs,
        long ReceivedUs,ulong Applied,IPEndPoint Destination,CancellationToken Token);
    private readonly Channel<Feedback> _feedback=Channel.CreateBounded<Feedback>(new BoundedChannelOptions(1)
    {
        SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.DropOldest,
        AllowSynchronousContinuations=false
    });
    private readonly object _gate = new();
    private readonly IOutputOwner _output;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(2);
    private readonly X509Certificate2 _certificate;
    private readonly Func<string, IPAddress, Task<bool>> _approve;
    private readonly Func<CancellationToken, Task>? _beforeFeedbackSend;
    private readonly List<Task> _clients = [];
    private TcpListener? _listener;
    private Socket? _udp;
    private Task[] _tasks = [];
    private ReceiveWorker? _ingress;
    private readonly Queue<string> _receiveFaults = new();
    private long _faultNumber, _retiredSuspensions, _retiredRecoveries;
    public long ReceiveFaultNumber => Interlocked.Read(ref _faultNumber);
    public string ReceiveTimingReport
    {
        get { lock (_gate) return "DriveBench Receiver0.3.8 receive timing\n" + (_ingress?.Report() ?? "not started") + $"\nRecovery lifetime: suspensions={_retiredSuspensions+(_active?.Input.SuspensionCount ?? 0)}, completed={_retiredRecoveries+(_active?.Input.RecoveryCount ?? 0)}; active-recovering={_active?.Input.Recovering ?? false};\nGC collection counts: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}\n" + string.Join("\n",_receiveFaults); }
    }
    private void FreezeReceive(string reason)
    {
        if(_receiveFaults.Count>=32) _receiveFaults.Dequeue();
        long now=MonoClock.Us;
        string state=_active is { } s ? $"net-epoch={s.Input.Epoch} armed={s.Input.Armed} recovering={s.Input.Recovering} recovery-left-us={Math.Max(0,s.Input.RecoveryUntilUs-now)} completed={s.Input.RecoveryCount} drop={s.Input.LastRejection}; " : "no active session; ";
        _receiveFaults.Enqueue($"{DateTimeOffset.UtcNow:O} pc-us={now} reason={reason}; " + state + (_ingress?.Report() ?? "not started"));
        Interlocked.Increment(ref _faultNumber);
    }
    private void Retire(Session s)
    {
        if(s.Retired) return;
        s.Retired=true;_retiredSuspensions+=s.Input.SuspensionCount;_retiredRecoveries+=s.Input.RecoveryCount;
    }
    private static void Notify(Session s) { try { s.Changed.Release(); } catch(SemaphoreFullException) { } }
    private Session? _active;
    private string? _bootstrap, _credential;
    private long _pairUntil, _generation;    private bool _pending;
    private string _message = "Waiting for the phone";
    public const int DefaultControlPort = 49174, DefaultInputPort = 49173;
    public int ControlPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public int InputPort => (_udp?.LocalEndPoint as IPEndPoint)?.Port ?? 0;
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(_certificate.RawData)).ToLowerInvariant();
    public RemoteReceiver(IOutputOwner output, Func<string, IPAddress, Task<bool>> approve) : this(output,approve,null) { }
    internal RemoteReceiver(IOutputOwner output, Func<string, IPAddress, Task<bool>> approve, Func<CancellationToken, Task>? beforeFeedbackSend)
    {
        _output = output; _approve = approve; _beforeFeedbackSend=beforeFeedbackSend;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=DriveBench local receiver", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        byte[] pfx = certificate.Export(X509ContentType.Pfx);
        try { _certificate = new X509Certificate2(pfx, (string?)null, OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    public void Start(IPAddress address, int controlPort = DefaultControlPort, int inputPort = DefaultInputPort)
    {
        if (_listener != null) throw new InvalidOperationException("Receiver already started");
        _listener = new TcpListener(address, controlPort); _listener.Start(2);
        _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveBufferSize = 16_384 };
        _udp.Bind(new IPEndPoint(address, inputPort));
        _ingress = new ReceiveWorker(_udp, ProcessDatagram, reason => StopInput(reason,includeTiming:true));
        _ingress.Start();
        _tasks = [Task.Run(AcceptLoop), Task.Run(FeedbackLoop), Task.Run(WatchLoop), _ingress.Completion];
    }
    public string OpenPairing(IPAddress address)
    {
        lock (_gate)
        {
            if(_active is { } previous) { Retire(previous); previous.Stop.Cancel(); } _active = null; _output.Disarm();
            _generation++; _pending = false; _credential = null;
            _bootstrap = Wire.Token(); _pairUntil = MonoClock.Us + 60_000_000;
            _message = "Scan this QR";
            return $"drivebench://pair?v=1&host={address}&port={ControlPort}&pin={Fingerprint}&token={_bootstrap}";
        }
    }
    public void StopInput(string reason, bool includeTiming = false)
    {
        lock (_gate) {
            if (_active is { } s) {
                if (!includeTiming || !s.Input.Faulted) {
                    if(includeTiming || s.Input.Armed) FreezeReceive(reason);
                    s.Input.Disarm(includeTiming ? s.Diagnostics.Explain(reason, MonoClock.Us) : reason, includeTiming);Notify(s);
                }
            }
            _output.Disarm(); _message = reason;
        }
    }
    public void OutputSourceExpired(long expiredDeadlineUs)
    {
        lock (_gate) {
            if (_active is not { } s || !s.Input.Armed || expiredDeadlineUs<=0 || s.Input.DeadlineUs>expiredDeadlineUs) return;
            if(s.Input.SuspendInput(MonoClock.Us,"PC source lease expired")) { FreezeReceive(s.Input.Reason);_output.Disarm();Notify(s); }
        }
    }
    public bool TryPrepareOutputRecovery(Action detach, bool allowArmed = false)
    {
        lock (_gate) {
            if (!allowArmed && _active is { } active && (active.Input.Armed || active.Input.Recovering)) return false;
            StopInput("PC controller recovery",includeTiming:true); detach(); return true;
        }
    }
    public ReceiverView View
    {
        get {
            lock (_gate) {
                if (_active is not { } s) return new(_message, false, false, false, 0, 0, 0, 0);
                bool sync = s.Samples >= 8 && s.Input.Clock.Valid(MonoClock.Us);
                return new(ConnectionReason(s, sync, _output.Availability), true, s.Input.Armed, sync, s.Input.Epoch, sync ? s.Input.Clock.Uncertainty(MonoClock.Us) / 1000 : 0, s.Input.Sequence, _output.AppliedNetworkSequence, s.Input.Recovering);
            }
        }
    }
    private static string ConnectionReason(Session session, bool sync, OutputAvailability output)
    {
        if (session.Input.Recovering || (!session.Input.Armed && session.Input.Faulted)) return session.Input.Reason;
        if (!output.Ready || (!session.Input.Armed && !output.CanArm)) return output.Reason;
        return sync ? session.Input.Reason : "Establishing Wi-Fi clock";
    }
    private async Task AcceptLoop()
    {
        try {
            while (!_stop.IsCancellationRequested) {
                var client = await _listener!.AcceptTcpClientAsync(_stop.Token);
                if (!_slots.Wait(0)) { client.Dispose(); continue; }
                Task task = Task.Run(async () => { try { await Serve(client); } finally { client.Dispose(); _slots.Release(); } });
                lock (_clients) { _clients.RemoveAll(t => t.IsCompleted); _clients.Add(task); } await Task.Delay(100, _stop.Token);
            }
        } catch { if (!_stop.IsCancellationRequested) StopInput("Control listener stopped",includeTiming:true); }
    }
    private async Task Serve(TcpClient client)
    {
        Session? session = null; long generation = 0; bool reserved = false;
        try {
            client.NoDelay = true; var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            using var tls = new SslStream(client.GetStream(), false);
            using var auth = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); auth.CancelAfter(TimeSpan.FromSeconds(30));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, ClientCertificateRequired = false }, auth.Token);
            JsonElement hello = await ControlFraming.ReadAsync(tls, auth.Token);
            if (hello.GetProperty("v").GetInt32() != 1) return;
            string presented = hello.GetProperty("token").GetString() ?? "";
            bool resume = hello.GetProperty("resume").GetBoolean();
            lock (_gate) {
                if (_active != null || _pending) throw new InvalidDataException("Active");
                bool valid = resume ? Wire.EqualToken(presented, _credential) : MonoClock.Us < _pairUntil && Wire.EqualToken(presented, _bootstrap);
                if (!valid) throw new InvalidDataException("Expired");
                generation = _generation; _pending = reserved = true;
            }
            if (!resume && !await _approve(hello.GetProperty("name").GetString() ?? "Phone", address).WaitAsync(TimeSpan.FromSeconds(20), auth.Token)) throw new InvalidDataException("Denied");
            string credential; lock (_gate) {
                if (_generation != generation || _active != null) throw new InvalidDataException("Changed");
                _bootstrap = null; _pending = false; reserved = false; _credential ??= Wire.Token(); credential = _credential;
                session = new Session(_stop.Token) { Address = address }; session.Input.RecoveryEnabled = true; _active = session; _output.Disarm();
            }
            await ControlFraming.WriteAsync(tls, new { type = "welcome", v = 1, session = session.Id, inputPort = InputPort, inputKey = Convert.ToBase64String(session.InKey), statusKey = Convert.ToBase64String(session.OutKey), credential, recovery = 2 }, auth.Token);
            while (!session.Stop.IsCancellationRequested) {
                using var step = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token); step.CancelAfter(TimeSpan.FromSeconds(2));
                long pc1 = MonoClock.Us; await ControlFraming.WriteAsync(tls, new { type = "tick", pc1 }, step.Token);
                var reply = await ControlFraming.ReadAsync(tls, step.Token); long pc4 = MonoClock.Us;
                lock (_gate) {
                    if (!ReferenceEquals(_active, session)) break;
                    session.Input.Clock.Observe(pc1, reply.GetProperty("phone2").GetInt64(), reply.GetProperty("phone3").GetInt64(), pc4);
                    session.Samples++; string command = reply.GetProperty("command").GetString() ?? "none";
                    if (command == "stop") { session.Input.Disarm("Stopped"); _output.Disarm(); }
                    else if (command == "arm") session.Input.Arm(reply.GetProperty("gyro").GetBoolean(), MonoClock.Us, _output.CanArm);
                }
                await session.Changed.WaitAsync(TimeSpan.FromMilliseconds(100), session.Stop.Token);
            }
        } catch { } finally {
            lock (_gate) {
                if (reserved && _generation == generation) _pending = false;
                if (session != null && ReferenceEquals(_active, session)) { Retire(session); _active = null; _output.Disarm(); }
            }
            if (session != null) session.Stop.Cancel();
        }
    }
    private void ProcessDatagram(byte[] buffer, IPEndPoint endpoint, long receivedUs)
    {
        lock (_gate) {
            var s = _active; if (s != null && endpoint.Address.Equals(s.Address) && InputPacket.TryDecode(buffer, s.InKey, out var p)) {
                long now = MonoClock.Us; if (s.Input.Accept(p, s.Id, now, out long deadline, _output.CanArm)) {
                    if (p.Armed && s.Input.Armed) _output.Publish(p.Report, deadline, p.Sequence);
                }
            }
        }
    }
    private async Task FeedbackLoop() { while (!_stop.IsCancellationRequested) await Task.Delay(100); }
    private async Task WatchLoop() { while (!_stop.IsCancellationRequested) { lock (_gate) if (_active is { } s) s.Input.Expire(MonoClock.Us); await Task.Delay(10); } }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _udp?.Dispose(); await Task.CompletedTask; }
}