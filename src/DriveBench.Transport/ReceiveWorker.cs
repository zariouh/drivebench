using System.Net;
using System.Net.Sockets;

namespace DriveBench.Transport;

// One producer and one consumer. The producer never takes an application lock or runs validation.
internal sealed class ReceiveWorker
{
    internal const int Capacity = 16;
    private sealed class Slot
    {
        internal readonly byte[] Bytes = new byte[InputPacket.Size];
        internal IPEndPoint? Endpoint;
        internal long ReceivedUs;
    }
    private readonly Slot[] _ring = Enumerable.Range(0, Capacity).Select(_ => new Slot()).ToArray();
    private readonly Socket _socket;
    private readonly Action<byte[], IPEndPoint, long> _process;
    private readonly Action<string> _failed;
    private readonly AutoResetEvent _available = new(false);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _reader, _consumer;
    private long _head, _tail, _received, _overflow, _invalid, _maxQueueUs, _maxWorkUs, _lastReceiveUs;
    private long _gateUs, _decodeUs, _admitUs, _outputUs, _stageAt;
    private string _stage = "idle";
    private int _running = 1, _remaining = 2;
    private Exception? _failure;
    internal ReceiveWorker(Socket socket, Action<byte[], IPEndPoint, long> process, Action<string> failed)
    { _socket = socket; _process = process; _failed = failed; }
    internal Task Completion => _done.Task;
    internal long Received => Interlocked.Read(ref _received);
    internal long Overflow => Interlocked.Read(ref _overflow);
    internal long Invalid => Interlocked.Read(ref _invalid);
    internal bool ReaderAlive => _reader?.IsAlive == true;
    internal bool ConsumerAlive => _consumer?.IsAlive == true;
    internal void Start()
    {
        _reader = new Thread(Read) { IsBackground = true, Name = "DriveBench UDP ingress" };
        _consumer = new Thread(Process) { IsBackground = true, Name = "DriveBench UDP admission" };
        // Modest per-thread request; no process-wide/realtime priority or busy spinning.
        try { _reader.Priority = ThreadPriority.AboveNormal; } catch (Exception ex) when (ex is PlatformNotSupportedException or System.Security.SecurityException) { }
        _consumer.Start(); _reader.Start();
    }
    internal void Stop()
    {
        Interlocked.Exchange(ref _running, 0);
        _socket.Dispose(); // Unblocks the synchronous receive on Windows and Linux.
        try { _available.Set(); } catch (ObjectDisposedException) { }
    }
    private static void Max(ref long target, long value)
    {
        long old;
        do { old = Interlocked.Read(ref target); if (value <= old) return; }
        while (Interlocked.CompareExchange(ref target, value, old) != old);
    }
    internal void Stage(string stage) { Interlocked.Exchange(ref _stageAt, MonoClock.Us); Volatile.Write(ref _stage, stage); }
    internal void Stages(long gate, long decode, long admit, long output)
    { Max(ref _gateUs,gate); Max(ref _decodeUs,decode); Max(ref _admitUs,admit); Max(ref _outputUs,output); }
    internal string Report()
    {
        long now = MonoClock.Us, last = Interlocked.Read(ref _lastReceiveUs);
        return $"received={Received} overflow={Overflow} invalid-size={Invalid} queued={Math.Clamp(Interlocked.Read(ref _head)-Interlocked.Read(ref _tail),0,Capacity)}/{Capacity}; " +
            $"last-socket-age-us={(last==0?-1:Math.Max(0,now-last))}; max-queue-us={Interlocked.Read(ref _maxQueueUs)} max-work-us={Interlocked.Read(ref _maxWorkUs)} " +
            $"max-gate-us={Interlocked.Read(ref _gateUs)} max-auth-us={Interlocked.Read(ref _decodeUs)} max-admission-us={Interlocked.Read(ref _admitUs)} max-output-us={Interlocked.Read(ref _outputUs)}; " +
            $"stage={Volatile.Read(ref _stage)} stage-age-us={Math.Max(0,now-Interlocked.Read(ref _stageAt))}; threads={ReaderAlive}/{ConsumerAlive}";
    }
    private void Read()
    {
        byte[] buffer = new byte[256];
        try
        {
            while (Volatile.Read(ref _running) != 0)
            {
                EndPoint endpoint = new IPEndPoint(IPAddress.Any,0);
                int count;
                try { count = _socket.ReceiveFrom(buffer, SocketFlags.None, ref endpoint); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
                { Interlocked.Increment(ref _invalid); continue; }
                long at = MonoClock.Us;
                Interlocked.Exchange(ref _lastReceiveUs, at); Interlocked.Increment(ref _received);
                if (count != InputPacket.Size) { Interlocked.Increment(ref _invalid); continue; }
                long head = _head;
                if (head - Volatile.Read(ref _tail) >= Capacity) { Interlocked.Increment(ref _overflow); continue; }
                var slot = _ring[(int)(head % Capacity)];
                buffer.AsSpan(0,count).CopyTo(slot.Bytes); slot.Endpoint = (IPEndPoint)endpoint; slot.ReceivedUs = at;
                Volatile.Write(ref _head,head+1); _available.Set();
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        { if (Volatile.Read(ref _running)!=0) Interlocked.CompareExchange(ref _failure,ex,null); }
        finally { Interlocked.Exchange(ref _running,0); _available.Set(); Finished(); }
    }
    private void Process()
    {
        byte[] buffer = new byte[InputPacket.Size];
        try
        {
            while (Volatile.Read(ref _running)!=0)
            {
                long tail = _tail;
                if (tail == Volatile.Read(ref _head)) { Stage("idle"); _available.WaitOne(); continue; }
                var slot = _ring[(int)(tail % Capacity)];
                slot.Bytes.CopyTo(buffer,0); var endpoint = slot.Endpoint!; long at = slot.ReceivedUs;
                Volatile.Write(ref _tail,tail+1); // Copy first: producer may now reuse this slot.
                long start = MonoClock.Us; Max(ref _maxQueueUs,Math.Max(0,start-at)); Stage("processing");
                _process(buffer,endpoint,at);
                Max(ref _maxWorkUs,Math.Max(0,MonoClock.Us-start)); Stage("idle");
            }
        }
        catch (Exception ex) { Interlocked.CompareExchange(ref _failure,ex,null); }
        finally
        {
            Interlocked.Exchange(ref _running,0); _socket.Dispose();
            if (_failure != null) _failed("Wi-Fi receive worker stopped; reopen receiver");
            Finished();
        }
    }
    private void Finished()
    {
        if (Interlocked.Decrement(ref _remaining)!=0) return;
        _available.Dispose();
        _done.TrySetResult();
    }
}
