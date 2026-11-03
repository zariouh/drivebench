using System;
using System.Buffers.Binary;
using System.IO;

namespace DriveBench.Capture;

public static class PcapngReader
{
    public static long Count(string path)
    {
        using var stream = File.OpenRead(path);
        return Count(stream);
    }
    public static long Count(Stream stream)
    {
        bool section = false, little = true; long packets = 0;
        Span<byte> header = stackalloc byte[8], fields = stackalloc byte[20], trailer = stackalloc byte[4];
        uint Read(ReadOnlySpan<byte> b) => little ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 12) throw new InvalidDataException("Truncated PCAPNG block");
            long start = stream.Position; stream.ReadExactly(header); int consumed = 8;
            bool isSection = BinaryPrimitives.ReadUInt32LittleEndian(header) == 0x0A0D0D0A;
            if (isSection)
            {
                stream.ReadExactly(trailer); consumed += 4;
                uint order = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
                if (order is not (0x1A2B3C4D or 0x4D3C2B1A)) throw new InvalidDataException("Invalid PCAPNG byte order");
                little = order == 0x1A2B3C4D; section = true;
            }
            if (!section) throw new InvalidDataException("PCAPNG section header missing");
            uint kind = Read(header), length = Read(header[4..]);
            if (length < 12 || length % 4 != 0 || length > stream.Length - start || (isSection && length < 28))
                throw new InvalidDataException("Invalid PCAPNG block bounds");
            if (kind is 6 or 2)
            {
                if (length < 32) throw new InvalidDataException("Truncated PCAPNG packet header");
                stream.ReadExactly(fields); consumed += 20;
                uint captured = Read(fields[12..]), original = Read(fields[16..]);
                if (captured > original || captured > length - 32) throw new InvalidDataException("Invalid PCAPNG packet length");
                if (captured > 0) packets++;
            }
            stream.Seek(length - consumed - 4, SeekOrigin.Current); stream.ReadExactly(trailer);
            if (Read(trailer) != length) throw new InvalidDataException("Mismatched PCAPNG block trailer");
        }
        if (!section) throw new InvalidDataException("Empty PCAPNG file");
        return packets;
    }
}