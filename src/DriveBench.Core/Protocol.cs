using System;
using System.Buffers.Binary;

namespace DriveBench.Core;

public readonly record struct PadReport(short Steering, byte Brake, byte Throttle)
{
    public static PadReport Neutral => default;
    public bool IsNeutral => this == Neutral;
    public static PadReport FromPercent(double steer, double brake, double throttle)
    {
        if (!double.IsFinite(steer) || !double.IsFinite(brake) || !double.IsFinite(throttle))
            throw new ArgumentOutOfRangeException(nameof(steer), "Nonfinite input");
        return new((short)Math.Round(Math.Clamp(steer, -100, 100) * 32767 / 100),
            (byte)Math.Round(Math.Clamp(brake, 0, 100) * 255 / 100),
            (byte)Math.Round(Math.Clamp(throttle, 0, 100) * 255 / 100));
    }
}

// Phase 1 LOCAL named-pipe protocol. This is not the future 88-byte LAN format.
public readonly record struct InputFrame(ulong Sequence, long SentTicks, long DeadlineTicks,
    uint Epoch, bool Armed, PadReport Report)
{
    public const int Size = 40;
    public const uint Magic = 0x31494244; // DBI1
    public byte[] Encode()
    {
        byte[] b = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic); b[4] = 1; b[5] = Armed ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16), SentTicks);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), DeadlineTicks);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(32), Report.Steering); b[34] = Report.Brake; b[35] = Report.Throttle;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36), Epoch);
        return b;
    }
    public static InputFrame Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size || BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic || b[4] != 1 ||
            b[5] > 1 || b[6] != 0 || b[7] != 0) throw new InvalidDataException("Invalid local frame");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(b[8..]), BinaryPrimitives.ReadInt64LittleEndian(b[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[24..]), BinaryPrimitives.ReadUInt32LittleEndian(b[36..]), b[5] == 1,
            new(BinaryPrimitives.ReadInt16LittleEndian(b[32..]), b[34], b[35]));
    }
}

public enum LeaseState { Idle, Armed, Expired, Disconnected }

// All methods are called under one host lock; time is supplied for deterministic tests.
public sealed class InputLease(long frequency)
{
    private readonly long _duration = frequency / 10; // 100 ms
    private ulong _sequence;
    private uint _epoch;
    private PadReport _report;
    private long _deadline;
    public LeaseState State { get; private set; } = LeaseState.Idle;
    public uint Epoch => _epoch;
    public ulong CurrentSequence => _sequence;
    public ulong LastAppliedSequence { get; private set; }

    public bool Accept(InputFrame frame, long now)
    {
        // Expire BEFORE accepting another frame: a new heartbeat cannot mask a stale lease.
        Sample(now);
        if (State == LeaseState.Disconnected || frame.Sequence <= _sequence || frame.Epoch == 0 ||
            frame.SentTicks < 0 || frame.SentTicks > now || frame.DeadlineTicks <= now ||
            frame.DeadlineTicks <= frame.SentTicks || frame.DeadlineTicks - frame.SentTicks > _duration ||
            frame.Report.Steering == short.MinValue) return false;
        if (!frame.Armed)
        {
            if (!frame.Report.IsNeutral || frame.Epoch < _epoch ||
                (State == LeaseState.Expired && frame.Epoch == _epoch)) return false;
            _epoch = frame.Epoch; _sequence = frame.Sequence; _report = default;
            State = LeaseState.Idle; return true;
        }
        if (frame.Epoch != _epoch || State == LeaseState.Expired) return false;
        _sequence = frame.Sequence; _report = frame.Report; _deadline = frame.DeadlineTicks;
        State = LeaseState.Armed; return true;
    }
    public PadReport Sample(long now)
    {
        if (State == LeaseState.Armed && now >= _deadline) { State = LeaseState.Expired; _report = default; }
        return State == LeaseState.Armed ? _report : default;
    }
    public void Submitted(ulong sequence) { if (sequence != 0) LastAppliedSequence = sequence; }
    public void Disconnect() { State = LeaseState.Disconnected; _report = default; }
}

public readonly record struct HostStatus(long CompletedTicks, ulong AppliedSequence, uint Epoch,
    PadReport Report, LeaseState State, bool RealDriver, bool ReadbackMatches)
{
    public const int Size = 40;
    public byte[] Encode()
    {
        byte[] b = new byte[Size]; BinaryPrimitives.WriteUInt32LittleEndian(b, 0x31534244); // DBS1
        b[4] = (byte)State; b[5] = RealDriver ? (byte)1 : (byte)0; b[6] = ReadbackMatches ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), CompletedTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), AppliedSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), Epoch);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(28), Report.Steering); b[30] = Report.Brake; b[31] = Report.Throttle;
        return b;
    }
    public static HostStatus Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size || BinaryPrimitives.ReadUInt32LittleEndian(b) != 0x31534244 ||
            b[4] > 3 || b[5] > 1 || b[6] > 1 || b[7] != 0 || b[32..].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Invalid host status");
        return new(BinaryPrimitives.ReadInt64LittleEndian(b[8..]), BinaryPrimitives.ReadUInt64LittleEndian(b[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[24..]), new(BinaryPrimitives.ReadInt16LittleEndian(b[28..]), b[30], b[31]),
            (LeaseState)b[4], b[5] != 0, b[6] != 0);
    }
}