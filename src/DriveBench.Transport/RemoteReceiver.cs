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

/// <summary>One phone, ephemeral trust, pinned TLS control, authenticated full-state UDP.</summary>
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
        get { lock (_gate) return "DriveBench Receiver0.3.8 receive timing\nLocal processing measurements, not radio-only. Times in microseconds. Maxima cover this receiver lifetime.\n" +
            (_ingress?.Report() ?? "not started") + $"\nRecovery lifetime: suspensions={_retiredSuspensions+(_active?.Input.SuspensionCount ?? 0)}, completed={_retiredRecoveries+(_active?.Input.RecoveryCount ?? 0)}; active-recovering={_active?.Input.Recovering ?? false}; window-us={Admission.RecoveryWindowUs}, healthy-reset-us={Admission.RecoveryStableUs}\nGC collection counts: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} (counts do not identify a pause cause)\n" + string.Join("\n",_receiveFaults); }
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
    private static void Notify(Session s)
    {
        // A coalesced state-change notification, never a periodic input/lease renewal.
        try { s.Changed.Release(); } catch(SemaphoreFullException) { }
    }
    private Session? _active;
    private string? _bootstrap, _credential;
    private long _pairUntil, _generation;
    private bool _pending;
    private string _message = "Waiting for the phone";
    public const int DefaultControlPort = 49174, DefaultInputPort = 49173;
    public int ControlPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public int InputPort => (_udp?.LocalEndPoint as IPEndPoint)?.Port ?? 0;
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(_certificate.RawData)).ToLowerInvariant();
    public RemoteReceiver(IOutputOwner output, Func<string, IPAddress, Task<bool>> approve) : this(output,approve,null) { }
    internal RemoteReceiver(IOutputOwner output, Func<string, IPAddress, Task<bool>> approve,
        Func<CancellationToken, Task>? beforeFeedbackSend)
    {
        _output = output; _approve = approve; _beforeFeedbackSend=beforeFeedbackSend;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=DriveBench local receiver", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Windows Schannel needs an OS-backed temporary user key; an EphemeralKeySet import cannot serve TLS there.
        byte[] pfx = certificate.Export(X509ContentType.Pfx);
        try { _certificate = X509CertificateLoader.LoadPkcs12(pfx, null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet); }
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
            _message = "Scan this QR in DriveBench within 60 seconds";
            return $"drivebench://pair?v=1&host={address}&port={ControlPort}&pin={Fingerprint}&token={_bootstrap}";
        }
    }
    public void StopInput(string reason, bool includeTiming = false)
    {
        lock (_gate)
        {
            if (_active is { } s)
            {
                // A late output callback must not erase an earlier, more specific input fault.
                if (!includeTiming || !s.Input.Faulted)
                {
                    if(includeTiming || s.Input.Armed) FreezeReceive(reason);
                    s.Input.Disarm(includeTiming ? s.Diagnostics.Explain(reason, MonoClock.Us) : reason, includeTiming);Notify(s);
                }
            }
            _output.Disarm(); _message = reason;
        }
    }
    // The Host reports the source deadline it actually expired. A late callback cannot cancel newer input.
    public void OutputSourceExpired(long expiredDeadlineUs)
    {
        lock (_gate)
        {
            if (_active is not { } s || !s.Input.Armed || expiredDeadlineUs<=0 ||
                s.Input.DeadlineUs>expiredDeadlineUs) return;
            if(s.Input.SuspendInput(MonoClock.Us,"PC source lease expired"))
            {
                FreezeReceive(s.Input.Reason);_output.Disarm();Notify(s);
            }
        }
    }
    // Re-check at the transition boundary: a TLS Resume may have won after the UI poll.
    public bool TryPrepareOutputRecovery(Action detach, bool allowArmed = false)
    {
        lock (_gate)
        {
            if (!allowArmed && _active is { } active && (active.Input.Armed || active.Input.Recovering)) return false;
            StopInput("PC controller recovery — tap Resume",includeTiming:true);
            detach(); // Output becomes unavailable before any concurrent arm can be accepted.
            return true;
        }
    }
    public ReceiverView View
    {
        get
        {
            lock (_gate)
            {
                if (_active is not { } s) return new(_message, false, false, false, 0, 0, 0, 0);
                bool sync = s.Samples >= 8 && s.Input.Clock.Valid(MonoClock.Us);
                return new(ConnectionReason(s, sync, _output.Availability), true, s.Input.Armed, sync, s.Input.Epoch,
                    sync ? s.Input.Clock.Uncertainty(MonoClock.Us) / 1000 : 0, s.Input.Sequence, _output.AppliedNetworkSequence, s.Input.Recovering);
            }
        }
    }
    private static string ConnectionReason(Session session, bool sync, OutputAvailability output)
    {
        // Preserve the first stop cause while the output performs its neutral handshake.
        if (session.Input.Recovering || (!session.Input.Armed && session.Input.Faulted)) return session.Input.Reason;
        if (!output.Ready || (!session.Input.Armed && !output.CanArm)) return output.Reason;
        if (!sync) return "Establishing a stable Wi-Fi clock";
        return session.Input.Reason;
    }
    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_stop.Token);
                if (!_slots.Wait(0)) { client.Dispose(); continue; }
                Task task = Task.Run(async () => { try { await Serve(client); } finally { client.Dispose(); _slots.Release(); } });
                lock (_clients) { _clients.RemoveAll(t => t.IsCompleted); _clients.Add(task); }
                await Task.Delay(100, _stop.Token); // cap unauthenticated TLS work, independently of UDP
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        { if (!_stop.IsCancellationRequested) StopInput("Control listener stopped; reopen receiver"); }
    }
    private async Task Serve(TcpClient client)
    {
        Session? session = null; long generation = 0; bool reserved = false;
        try
        {
            client.NoDelay = true;
            var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            using var tls = new SslStream(client.GetStream(), false);
            using var auth = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); auth.CancelAfter(TimeSpan.FromSeconds(30));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, ClientCertificateRequired = false }, auth.Token);
            JsonElement hello = await ControlFraming.ReadAsync(tls, auth.Token);
            if (hello.GetProperty("v").GetInt32() != 1 || !hello.TryGetProperty("recovery", out var recovery) || recovery.GetInt32() != 2)
            {
                const string incompatible="Install Android 0.3.6 and PC receiver 0.3.8 together";
                lock(_gate) _message=incompatible;
                await ControlFraming.WriteAsync(tls,new { type="error",reason=incompatible },auth.Token);
                return;
            }
            string presented = hello.GetProperty("token").GetString() ?? "";
            bool resume = hello.GetProperty("resume").GetBoolean();
            string name = hello.GetProperty("name").GetString() ?? "Phone";
            name = new string(name.Where(c => !char.IsControl(c)).Take(60).ToArray());
            lock (_gate)
            {
                if (_active != null || _pending) throw new InvalidDataException("Receiver already has a phone");
                bool valid = resume ? Wire.EqualToken(presented, _credential) : MonoClock.Us < _pairUntil && Wire.EqualToken(presented, _bootstrap);
                if (!valid) throw new InvalidDataException("Pairing expired; scan a new QR");
                generation = _generation; _pending = reserved = true;
            }
            if (!resume && !await _approve(name, address).WaitAsync(TimeSpan.FromSeconds(20), auth.Token)) throw new InvalidDataException("Pairing was not approved");
            string credential;
            lock (_gate)
            {
                if (_generation != generation || _active != null || (!resume && MonoClock.Us >= _pairUntil)) throw new InvalidDataException("Pairing changed or expired");
                _bootstrap = null; _pending = false; reserved = false;
                _credential ??= Wire.Token(); credential = _credential;
                session = new Session(_stop.Token) { Address = address }; session.Input.RecoveryEnabled = true; if (session.Id == 0) session.Id = 1;
                _active = session; _output.Disarm();
            }
            await ControlFraming.WriteAsync(tls, new { type = "welcome", v = 1, session = session.Id, inputPort = InputPort, 
                inputKey = Convert.ToBase64String(session.InKey), statusKey = Convert.ToBase64String(session.OutKey), credential, 
                receiverVersion = "0.3.8", recovery = 2, recoveryWindowUs = Admission.RecoveryWindowUs, recoveryStableUs = Admission.RecoveryStableUs, inputBytes = 88, statusBytes = 80, inputHz = 60, uiMaxUs = 50_000, sensorMaxUs = 50_000 }, auth.Token);
            while (!session.Stop.IsCancellationRequested)
            {
                using var step = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token); step.CancelAfter(TimeSpan.FromSeconds(2));
                long pc1 = MonoClock.Us;
                await ControlFraming.WriteAsync(tls, new { type = "tick", pc1 }, step.Token);
                var reply = await ControlFraming.ReadAsync(tls, step.Token); long pc4 = MonoClock.Us;
                string command = reply.GetProperty("command").GetString() ?? "none";
                long id = reply.GetProperty("request").GetInt64();
                object ack;
                lock (_gate)
                {
                    if (!ReferenceEquals(_active, session)) break;
                    session.Input.Clock.Observe(pc1, reply.GetProperty("phone2").GetInt64(), reply.GetProperty("phone3").GetInt64(), pc4);
                    session.Samples++;
                    if (id > session.LastCommand)
                    {
                        session.LastCommand = id;
                        if (command == "stop") { if(session.Input.Armed) FreezeReceive("Phone requested Stop over TLS"); session.Input.Disarm("Stopped — tap Resume on the phone"); _output.Disarm(); }
                        else if (command == "suspend")
                        {
                            if(session.Input.Armed && session.Input.SuspendInput(MonoClock.Us,"Phone requested temporary neutral")) _output.Disarm();
                        }
                        else if (command == "arm" && session.Samples >= 8)
                        {
                            if(session.Input.Arm(reply.GetProperty("gyro").GetBoolean(), MonoClock.Us, _output.CanArm))
                                session.Diagnostics.ResetRejection();
                        }
                        else if (command != "none" && command != "arm") throw new InvalidDataException("Unsupported command");
                    }
                    ExpireInput(session, MonoClock.Us);
                    bool sync = session.Samples >= 8 && session.Input.Clock.Valid(pc4);
                    var availability = _output.Availability;
                    while(session.Changed.Wait(0)) { } // This ACK includes all transitions observed under the gate.
                    ack = new { type = "ack", epoch = session.Input.Epoch, armed = session.Input.Armed, recovering = session.Input.Recovering, 
                        sync, ready = availability.Ready && (session.Input.Armed || availability.CanArm), real = _output.Status.RealDriver, command = session.LastCommand,
                        reason = ConnectionReason(session, sync, availability) };
                }
                await ControlFraming.WriteAsync(tls, ack, step.Token);
                await session.Changed.WaitAsync(TimeSpan.FromMilliseconds(session.Samples < 8 ? 5 : 100),session.Stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or TimeoutException or FormatException)
        { /* bounded peer failures do not terminate the listener; no credentials are logged */ }
        finally
        {
            lock (_gate)
            {
                if (reserved && _generation == generation) _pending = false;
                if (session != null && ReferenceEquals(_active, session))
                { Retire(session); _active = null; _output.Disarm(); _message = "Phone disconnected — tap Reconnect, then Resume"; }
            }
            if (session != null) { session.Stop.Cancel(); session.Stop.Dispose(); session.Changed.Dispose(); CryptographicOperations.ZeroMemory(session.InKey); CryptographicOperations.ZeroMemory(session.OutKey); }
        }
    }
    private void ProcessDatagram(byte[] buffer, IPEndPoint endpoint, long receivedUs)
    {
        Feedback? feedback = null;
        long waiting = MonoClock.Us, entered=waiting, decoded=waiting, admitted=waiting, published=waiting;
        _ingress!.Stage("gate");
        lock (_gate)
        {
            entered = MonoClock.Us; decoded=admitted=published=entered;
            var s = _active;
            _ingress.Stage("auth");
            if (s != null && endpoint.Address.Equals(s.Address) && 
                (s.Endpoint == null || s.Endpoint.Equals(endpoint)) && InputPacket.TryDecode(buffer, s.InKey, out var packet))
            {
                decoded = MonoClock.Us;
                _ingress.Stage("admission");
                // Original source timestamps govern freshness. Ingress time is diagnostic only.
                long now = MonoClock.Us;
                uint before = s.Input.Epoch; bool wasArmed = s.Input.Armed;
                bool accepted = s.Input.Accept(packet, s.Id, now, out long deadline, _output.CanArm);
                admitted = MonoClock.Us;
                if (before != s.Input.Epoch)
                {
                    if(wasArmed) FreezeReceive(s.Input.Reason + $" packet={packet.Sequence} socket-us={receivedUs}");
                    if(s.Input.Faulted) s.Input.AnnotateInterruption(s.Diagnostics.Explain(s.Input.Reason,now));
                    _output.Disarm();Notify(s);
                }
                if(!wasArmed && s.Input.Armed) { FreezeReceive("Automatically recovered from fresh input");Notify(s); }
                s.Diagnostics.Observe(now,accepted,s.Input.LastRejection);
                _ingress.Stage("output");
                if (accepted)
                {
                    if (s.Endpoint == null && !packet.Armed && packet.Neutral) s.Endpoint = endpoint;
                    if (s.Endpoint != null && packet.Armed && s.Input.Armed) _output.Publish(packet.Report, deadline, packet.Sequence);
                    published = MonoClock.Us;
                }
                if (s.Endpoint != null && packet.Session == s.Id && now - s.LastStatusUs >= 50_000)
                {
                        s.LastStatusUs = now; ushort flags = (ushort)((s.Input.Armed ? 1 : 0) | (_output.Ready ? 2 : 0) | 
                            (s.Input.Faulted ? 4 : 0) | (s.Samples >= 8 && s.Input.Clock.Valid(now) ? 8 : 0) | (s.Input.Recovering ? 16 : 0) | (!accepted ? 32 : 0));
                        feedback = new Feedback(s.Id, ++s.StatusSequence, flags, packet.Sequence, packet.SentUs, 
                            now, _output.AppliedNetworkSequence,s.Endpoint,s.Stop.Token);
                }
                published = MonoClock.Us;
            }
            else decoded=admitted=published=MonoClock.Us;
        }
        _ingress.Stages(entered-waiting, decoded-entered, admitted-decoded, published-admitted);
        if (feedback != null) _feedback.Writer.TryWrite(feedback);
    }
    private async Task FeedbackLoop()
    {
        try
        {
            long lastSendUs=0;
            await foreach(var queued in _feedback.Reader.ReadAllAsync(_stop.Token))
            {
                var feedback=queued;
                if(feedback.Token.IsCancellationRequested) continue;
                lock(_gate) { if(_active?.Id!=feedback.Session) continue; }
                try
                {
                    long waitUs=50_000-(MonoClock.Us-lastSendUs);
                    if(waitUs>0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(waitUs/1000d)),feedback.Token);
                    if(_beforeFeedbackSend!=null) await _beforeFeedbackSend(feedback.Token);
                    feedback.Token.ThrowIfCancellationRequested();
                    // Prefer the latest waiting status after congestion; never build a backlog.
                    while(_feedback.Reader.TryRead(out var newer)) feedback=newer;
                    if(feedback.Token.IsCancellationRequested) continue;
                    byte[] bytes;
                    lock(_gate)
                    {
                        if(_active is not { } active || active.Id!=feedback.Session) continue;
                        // Stamp/sign at send time, rather than at enqueue time.
                        bytes=Wire.Status(feedback.Session,feedback.Sequence,feedback.Flags,feedback.InputSequence, 
                            feedback.InputSentUs,feedback.ReceivedUs,feedback.Applied,active.OutKey);
                    }
                    lastSendUs=MonoClock.Us;
                    await _udp!.SendToAsync(bytes,SocketFlags.None,feedback.Destination,feedback.Token);
                }
                catch(OperationCanceledException) when(feedback.Token.IsCancellationRequested || _stop.IsCancellationRequested) { }
                catch(SocketException)
                {
                    lock(_gate)
                        if(_active?.Id==feedback.Session && !_stop.IsCancellationRequested)
                            StopInput("PC feedback send failed",includeTiming:true);
                }
                catch(ObjectDisposedException) when(_stop.IsCancellationRequested) { }
            }
        }
        catch(OperationCanceledException) when(_stop.IsCancellationRequested) { }
    }
    private void ExpireInput(Session s,long now)
    {
        if(!s.Input.Expire(now)) return;
        FreezeReceive(s.Input.Reason);
        s.Input.AnnotateInterruption(s.Diagnostics.Explain(s.Input.Reason,now));
        _output.Disarm();Notify(s);
    }
    private async Task WatchLoop()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (await timer.WaitForNextTickAsync(_stop.Token))
                lock (_gate) { if (_active is { } s) ExpireInput(s, MonoClock.Us); }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _feedback.Writer.TryComplete(); lock (_gate) { _active?.Stop.Cancel(); _output.Disarm(); }
        _listener?.Stop(); _ingress?.Stop(); _udp?.Dispose();
        try { await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { throw new IOException("Receive workers did not stop; receiver resources retained for safe shutdown"); } catch (OperationCanceledException) { }
        Task[] clients; lock (_clients) clients = _clients.ToArray();
        try { await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _certificate.Dispose(); _stop.Dispose();
    }
}