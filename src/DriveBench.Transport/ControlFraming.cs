using System.Buffers.Binary;
using System.Text.Json;

namespace DriveBench.Transport;

public static class ControlFraming
{
    public const int Maximum = 4096;
    public static async Task<JsonElement> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[2]; await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length is < 2 or > Maximum) throw new InvalidDataException("Invalid control record size");
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<JsonElement>(bytes, new JsonSerializerOptions { MaxDepth = 8 });
    }
    public static async Task WriteAsync(Stream stream, object value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > Maximum) throw new InvalidDataException("Control record too large");
        byte[] header = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
}
