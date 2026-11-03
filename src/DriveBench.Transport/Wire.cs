#nullable enable
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using DriveBench.Core;

namespace DriveBench.Transport;

public static class MonoClock
{
    public static long Us => (long)(Stopwatch.GetTimestamp() * (1_000_000d / Stopwatch.Frequency));
    public static long Ticks(long us) => (long)(us * (Stopwatch.Frequency / 1_000_000d));
}

public readonly record struct InputPacket(ushort Flags, ulong Session, ulong Sequence, long SentUs,
    long SampleUs, uint Epoch, short Steering, ushort Brake, ushort Throttle, uint SensorAgeUs, uint UiAgeUs)
{
    public const int Size = 88;
    public bool Armed => (Flags & 1) != 0;
    public bool Gyro => (Flags & 8) != 0;
    public bool Stop => (Flags & 16) != 0;
    public bool Suspend => (Flags & 32) != 0;
    public bool Neutral => Steering == 0 && Brake == 0 && Throttle == 0;
    public PadReport Report => new(Steering, (byte)((Brake + 128) / 257), (byte)((Throttle + 128) / 257));
    public byte[] Encode(byte[] key)
    {
        byte[] b = new byte[Size]; "DRC1"u8.CopyTo(b); b[4] = 1; b[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), Session);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), SentUs);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(32), SampleUs);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), Epoch);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(48), Steering);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), Brake);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(58), Throttle);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(60), SensorAgeUs);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(64), UiAgeUs);
        Wire.Sign(b, key, 72); return b;
    }
    public static bool TryDecode(ReadOnlySpan<byte> b, byte[] key, out InputPacket p)
    {
        p = default;
        if (b.Length != Size || !b[..4].SequenceEqual("DRC1"u8) || b[4] != 1 || b[5] != 1 ||
            !Wire.Authentic(b, key, 72)) return false;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(b[6..]);
        if ((flags & ~63) != 0 || b[44..48].IndexOfAnyExcept((byte)0) >= 0 ||
            b[50..56].IndexOfAnyExcept((byte)0) >= 0 || b[68..72].IndexOfAnyExcept((byte)0) >= 0) return false;
        p = new(flags, BinaryPrimitives.ReadUInt64LittleEndian(b[8..]), BinaryPrimitives.ReadUInt64LittleEndian(b[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[24..]), BinaryPrimitives.ReadInt64LittleEndian(b[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[40..]), BinaryPrimitives.ReadInt16LittleEndian(b[48..]),
            BinaryPrimitives.ReadUInt16LittleEndian(b[56..]), BinaryPrimitives.ReadUInt16LittleEndian(b[58..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[60..]), BinaryPrimitives.ReadUInt32LittleEndian(b[64..]));
        return p.Sequence > 0 && p.Sequence <= long.MaxValue && p.SentUs > 0 && p.SampleUs > 0 &&
            p.SampleUs <= p.SentUs && p.Steering != short.MinValue;
    }
}

public static class Wire
{
    public static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static bool EqualToken(string? a, string? b) => a is { Length: 43 } && b is { Length: 43 } &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));
    public static void Sign(byte[] bytes, byte[] key, int prefix) => HMACSHA256.HashData(key, bytes.AsSpan(0, prefix)).AsSpan(0, 16).CopyTo(bytes.AsSpan(prefix));
    public static bool Authentic(ReadOnlySpan<byte> bytes, byte[] key, int prefix) =>
        key.Length == 32 && bytes.Length == prefix + 16 && CryptographicOperations.FixedTimeEquals(
            HMACSHA256.HashData(key, bytes[..prefix]).AsSpan(0, 16), bytes[prefix..]);
    public static byte[] Status(ulong session, ulong sequence, ushort flags, ulong echoedSequence, long echoedUs,
        long receivedUs, ulong appliedSequence, byte[] key)
    {
        byte[] b = new byte[80]; "DRC1"u8.CopyTo(b); b[4] = 1; b[5] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), session);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), echoedSequence);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(32), echoedUs);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(40), receivedUs);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(48), MonoClock.Us);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(56), appliedSequence);
        Sign(b, key, 64); return b;
    }
}

// PC-minus-phone interval, derived entirely from the PC's own sync round trips.
public sealed class ClockModel
{
    private double _offset, _uncertainty;
    private long _at;
    public bool Valid(long now) => _at > 0 && now >= _at && now - _at <= 10_000_000 && Uncertainty(now) <= 10_000;
    public double Uncertainty(long now) => _uncertainty + Math.Max(0, now - _at) * .0005; // policy: 500 ppm, not a hardware measurement
    public bool Observe(long pc1, long phone2, long phone3, long pc4)
    {
        if (pc1 <= 0 || pc4 < pc1 || phone2 <= 0 || phone3 < phone2) return false;
        long rtt = (pc4 - pc1) - (phone3 - phone2);
        if (rtt < 0 || rtt > 18_000) return false;
        double offset = ((pc1 - (double)phone2) + (pc4 - (double)phone3)) / 2;
        double uncertainty = rtt / 2d + 1_000;
        if (_at != 0 && pc4 < _at) return false;
        if (_at != 0 && Valid(pc4))
        {
            // Both intervals bound the same offset at pc4. Preserve all compatible evidence;
            // periodic replacement by a noisy asymmetric sample can needlessly age input.
            double previousUncertainty = Uncertainty(pc4);
            double lower = Math.Max(_offset - previousUncertainty, offset - uncertainty);
            double upper = Math.Min(_offset + previousUncertainty, offset + uncertainty);
            if (lower > upper) { _at = 0; return false; }
            offset = lower + (upper - lower) / 2;
            uncertainty = (upper - lower) / 2;
        }
        _offset = offset; _uncertainty = uncertainty; _at = pc4;
        return Valid(pc4);
    }
    public bool ProducedAfter(InputPacket p, long pcUs, long now)
    {
        long source = Math.Min(p.SampleUs,p.SentUs-p.UiAgeUs);
        if(p.Gyro) source=Math.Min(source,p.SentUs-p.SensorAgeUs);
        return Valid(now) && source + _offset - Uncertainty(now) > pcUs;
    }
    public bool Deadline(InputPacket p, long now, out long deadline) => Deadline(p, now, out deadline, out _);
    public bool Deadline(InputPacket p, long now, out long deadline, out string rejection)
    {
        deadline = 0; rejection = "None";
        if (!Valid(now)) { rejection = "Clock"; return false; }
        double uncertainty = Uncertainty(now), earliest = p.SentUs + _offset - uncertainty;
        if (earliest > now) { rejection = "Future"; return false; }
        if (now - earliest > 40_000) { rejection = "PacketAge"; return false; }
        long source = Math.Min(p.SampleUs, p.SentUs - p.UiAgeUs);
        if (p.Gyro) source = Math.Min(source, p.SentUs - p.SensorAgeUs);
        deadline = Math.Min(now + 100_000, (long)(source + _offset - uncertainty + 100_000));
        if (deadline <= now) { rejection = "SourceAge"; return false; }
        return true;
    }
}

public sealed class Admission
{
    // Intent recovery while neutral is distinct from the unchanged100ms active source lease.
    // The episode must outlast the healthy reset interval, or a second brief gap can latch by construction.
    public const long RecoveryWindowUs = 1_000_000, RecoveryStableUs = 500_000;
    public uint Epoch { get; private set; } = 1;
    public bool Armed { get; private set; }
    public bool RecoveryEnabled { get; set; }
    public bool Recovering { get; private set; }
    public long RecoveryUntilUs { get; private set; }
    public long ChallengeUs { get; private set; }
    public int RecoveryCount { get; private set; }
    public int SuspensionCount { get; private set; }
    private bool _intent;
    private long _episodeUntil, _healthySince;
    public bool SuspendInput(long now, string reason)
    {
        if(!RecoveryEnabled || !_intent) { Disarm(reason,true); return true; }
        if(Recovering) return false; // Requests cannot renew a recovery window.
        if(_episodeUntil==0) _episodeUntil=now+RecoveryWindowUs;
        if(now>=_episodeUntil) { Disarm("Connection remains unstable — tap Resume",true); return true; }
        if(Epoch==uint.MaxValue) throw new InvalidDataException("Epoch exhausted; reconnect");
        Epoch++;SuspensionCount++;Armed=false;Recovering=true;DeadlineUs=0;LastNeutralUs=0;Faulted=false;
        ChallengeUs=now;RecoveryUntilUs=_episodeUntil;Reason="Recovering — waiting for fresh controls";
        return true;
    }
    public bool Gyro { get; private set; }
    public ulong Sequence { get; private set; }
    public long DeadlineUs { get; private set; }
    public long LastNeutralUs { get; private set; }
    public long LastReceivedUs { get; private set; }
    public long EchoedUs { get; private set; }
    public bool Faulted { get; private set; }
    public string LastRejection { get; private set; } = "None";
    public string Reason { get; private set; } = "Connected — tap Resume on the phone";
    public readonly ClockModel Clock = new();
    public void Disarm(string reason, bool fault = false)
    {
        if (Epoch == uint.MaxValue) throw new InvalidDataException("Epoch exhausted; reconnect");
        _intent=false;Recovering=false;RecoveryUntilUs=0;_episodeUntil=0;_healthySince=0;
        Epoch++; Armed = false; DeadlineUs = 0; LastNeutralUs = 0; Faulted = fault; Reason = reason;
    }
    public bool Expire(long now)
    {
        if((Armed || Recovering) && !Clock.Valid(now)) { Disarm("Input clock confidence lost",true);return true; }
        if(Recovering && now>=RecoveryUntilUs) { Disarm("Interruption lasted too long — tap Resume",true);return true; }
        if(Armed && now>=DeadlineUs) return SuspendInput(now,"Input source deadline expired");
        return false;
    }
    internal void AnnotateInterruption(string reason) { if (!Armed) Reason = reason; }
    public bool Arm(bool gyro, long now, bool driverReady)
    {
        Expire(now);
        if (Armed || Recovering || !driverReady || !Clock.Valid(now) || LastNeutralUs == 0 || now - LastNeutralUs > 100_000 || Gyro != gyro)
        { Reason = "Waiting for fresh neutral controls and a stable connection"; return false; }
        Epoch++; Armed = true; _intent=true;_episodeUntil=0;_healthySince=now; DeadlineUs = now + 100_000; Faulted = false; Reason = "Driving"; return true;
    }
    public bool Accept(InputPacket p, ulong session, long now, out long deadline, bool driverReady = true)
    {
        deadline = 0; LastRejection = "None"; Expire(now);
        if (p.Session != session) { LastRejection = "Session"; return false; }
        if (p.Sequence <= Sequence) { LastRejection = "Sequence"; return false; }
        // Authenticated Stop is safe to honor even when clock confidence has been lost.
        if (p.Stop) { LastRejection = "Stop"; Sequence = p.Sequence; Disarm("Stopped — tap Resume on the phone"); return false; }
        // A Stop from immediately before a recovery challenge still cancels intent; replayed sequences do not.
        if (p.Epoch != Epoch) { LastRejection = "Epoch"; return false; }
        if(p.Suspend)
        {
            Sequence=p.Sequence;LastRejection="Suspended";
            if(Armed) SuspendInput(now,"Phone input temporarily unavailable");
            return false;
        }
        if (!Clock.Deadline(p, now, out deadline, out string timing)) { LastRejection = timing; return false; }
        if ((p.Flags & 2) == 0 || p.UiAgeUs > 50_000 || p.SentUs - p.SampleUs > 50_000 || 
            (p.Gyro && ((p.Flags & 4) == 0 || p.SensorAgeUs > 50_000)))
        { LastRejection = "PhoneAge"; if (Armed) SuspendInput(now,"Fresh phone input stopped — tap Resume"); return false; }
        Sequence = p.Sequence; LastReceivedUs = now; EchoedUs = p.SentUs;
        if (!p.Armed)
        {
            if (Armed) { LastRejection = "Released"; Disarm("Stopped — tap Resume on the phone"); return false; }
            if (!p.Neutral) { LastRejection = "NotNeutral"; return false; }
            Gyro = p.Gyro; LastNeutralUs = now; return true;
        }
        if(Recovering)
        {
            if(p.Gyro!=Gyro || !Clock.ProducedAfter(p,ChallengeUs,now)) { LastRejection="RecoverySource";return false; }
            if(!driverReady) { LastRejection="HostNeutral";return false; }
            Armed=true;Recovering=false;Faulted=false;_healthySince=now;RecoveryCount++;Reason="Driving";
        }
        if(Armed && _episodeUntil!=0 && now-_healthySince>=RecoveryStableUs) _episodeUntil=0;
        if (!Armed || p.Gyro != Gyro) { LastRejection = "NotArmedOrMode"; return false; }
        DeadlineUs = deadline; return true;
    }
}