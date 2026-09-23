using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TwinDesk;

public enum PacketKind : byte { Hello = 1, Audio = 2, Command = 3, Reply = 4, Heartbeat = 5, Welcome = 6, Input = 7 }
public record Packet(PacketKind Kind, byte[] Data);

public static class Wire
{
    public const int MaxPacket = 65536;
    public static async Task<Packet> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 1 || length > MaxPacket) throw new InvalidDataException("Invalid packet length.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        if (!Enum.IsDefined((PacketKind)body[0])) throw new InvalidDataException("Unknown packet type.");
        return new Packet((PacketKind)body[0], body[1..]);
    }

    // Each connection's writer serializes calls; a complete frame is a single write.
    public static async Task WriteAsync(Stream stream, PacketKind kind, byte[] data, CancellationToken ct)
    {
        if (data.Length >= MaxPacket) throw new InvalidDataException("Packet too large.");
        var frame = new byte[data.Length + 5];
        BinaryPrimitives.WriteInt32BigEndian(frame, data.Length + 1);
        frame[4] = (byte)kind;
        data.CopyTo(frame, 5);
        await stream.WriteAsync(frame, ct);
    }

    public static bool TokenMatches(string expected, string? actual) => actual is not null &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                                                SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
    public static bool ValidAudio(byte[] data) => data.Length is > 0 and <= 19200 && data.Length % 4 == 0;
}

public enum Computer { PC, Mac }
