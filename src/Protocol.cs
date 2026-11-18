using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Amethyst;

// This layer is deliberately independent of the login/status handlers.
// Minecraft: VarInt-framed packets, big-endian fixed-size numbers, UTF-8 strings.
public static class Protocol
{
    public static bool TracePackets { get; set; }
    public const int Version = 777;
    public const string VersionName = "26.3";
    public const int MaxPacketBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<Packet?> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        int? length = await ReadVarIntFromStreamAsync(stream, ct);
        if (length is null) return null;
        if (length.Value is < 1 or > MaxPacketBytes)
            throw new InvalidDataException($"Invalid packet length {length.Value}.");

        var bytes = new byte[length.Value];
        await stream.ReadExactlyAsync(bytes, ct);
        var reader = new PacketReader(bytes);
        int id = reader.ReadVarInt();
        if (TracePackets)
            Console.WriteLine($"[wire <-] 0x{id:X2} payload={bytes.Length - reader.Position} bytes");
        return new Packet(id, bytes.AsMemory(reader.Position));
    }

    public static async Task WritePacketAsync(Stream stream, int id,
        Action<PacketWriter>? writeFields, CancellationToken ct)
    {
        var writer = new PacketWriter();
        writer.WriteVarInt(id);
        writeFields?.Invoke(writer);
        byte[] body = writer.ToArray();
        if (body.Length > MaxPacketBytes)
            throw new InvalidDataException("Packet exceeds maximum length.");

        var header = new PacketWriter();
        header.WriteVarInt(body.Length);
        byte[] prefix = header.ToArray();
        var frame = new byte[prefix.Length + body.Length];
        prefix.CopyTo(frame, 0);
        body.CopyTo(frame, prefix.Length);
        // One write per frame avoids interleaving the length and body if a
        // heartbeat task writes at the same time as a packet handler.
        if (TracePackets)
            Console.WriteLine($"[wire ->] 0x{id:X2} frameBody={body.Length} bytes");
        await stream.WriteAsync(frame, ct);
    }

    private static async Task<int?> ReadVarIntFromStreamAsync(Stream stream, CancellationToken ct)
    {
        int result = 0;
        var b = new byte[1];
        for (int i = 0; i < 5; i++)
        {
            int read = await stream.ReadAsync(b, ct);
            if (read == 0)
            {
                if (i == 0) return null;
                throw new EndOfStreamException("EOF inside packet VarInt.");
            }
            if (i == 4 && (b[0] & 0xF0) != 0)
                throw new InvalidDataException("VarInt overflows 32 bits.");
            result |= (b[0] & 0x7F) << (7 * i);
            if ((b[0] & 0x80) == 0) return result;
        }
        throw new InvalidDataException("VarInt too long.");
    }

    internal static string DecodeUtf8(byte[] bytes) => Utf8.GetString(bytes);
    internal static byte[] EncodeUtf8(string text) => Utf8.GetBytes(text);
}

public readonly record struct Packet(int Id, ReadOnlyMemory<byte> Fields)
{
    public PacketReader Reader() => new(Fields);
}

public sealed class PacketReader
{
    private readonly ReadOnlyMemory<byte> _data;
    public int Position { get; private set; }
    public int Remaining => _data.Length - Position;

    public PacketReader(ReadOnlyMemory<byte> data) => _data = data;

    private ReadOnlySpan<byte> Take(int length)
    {
        if (length < 0 || length > Remaining)
            throw new InvalidDataException("Truncated packet.");
        var data = _data.Span.Slice(Position, length);
        Position += length;
        return data;
    }

    public byte ReadByte() => Take(1)[0];
    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    public short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(Take(2));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleBigEndian(Take(8));
    public float ReadFloat() => BinaryPrimitives.ReadSingleBigEndian(Take(4));
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public byte[] ReadFixedBytes(int count) => Take(count).ToArray();
    public byte[] ReadByteArray(int limit)
    {
        int n = ReadVarInt();
        if (n is < 0 || n > limit) throw new InvalidDataException("Byte array too large.");
        return Take(n).ToArray();
    }
    public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));
    public Guid ReadUuid() => new(Take(16), bigEndian: true);

    public int ReadVarInt()
    {
        int result = 0;
        for (int i = 0; i < 5; i++)
        {
            int b = ReadByte();
            if (i == 4 && (b & 0xF0) != 0)
                throw new InvalidDataException("VarInt overflows 32 bits.");
            result |= (b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("VarInt too long.");
    }

    public string ReadString(int maxCharacters)
    {
        int length = ReadVarInt();
        if (length < 0 || length > maxCharacters * 3)
            throw new InvalidDataException("String length invalid.");
        var bytes = Take(length).ToArray();
        var text = Protocol.DecodeUtf8(bytes);
        if (text.Length > maxCharacters)
            throw new InvalidDataException("String character limit exceeded.");
        return text;
    }

    public void RequireEnd()
    {
        if (Remaining != 0) throw new InvalidDataException("Unexpected trailing packet fields.");
    }
}

public sealed class PacketWriter
{
    private readonly MemoryStream _stream = new();

    public byte[] ToArray() => _stream.ToArray();
    public void WriteByte(byte value) => _stream.WriteByte(value);
    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);
    public void WriteBytes(ReadOnlySpan<byte> value) => _stream.Write(value);
    public void WriteByteArray(ReadOnlySpan<byte> value)
    {
        WriteVarInt(value.Length);
        WriteBytes(value);
    }

    public void WriteInt16(short value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(b, value);
        _stream.Write(b);
    }

    public void WriteInt32(int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        _stream.Write(b);
    }

    public void WriteDouble(double value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, value);
        _stream.Write(b);
    }

    public void WriteFloat(float value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(b, value);
        _stream.Write(b);
    }

    public void WritePosition(int x, int y, int z)
    {
        long packed = ((long)(x & 0x3FFFFFF) << 38)
                    | ((long)(z & 0x3FFFFFF) << 12)
                    | (uint)(y & 0xFFF);
        WriteInt64(packed);
    }

    public void WriteVarInt(int number)
    {
        uint value = unchecked((uint)number);
        while ((value & ~0x7FU) != 0)
        {
            _stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        _stream.WriteByte((byte)value);
    }

    // 26.3 world-clock updates carry time as a signed VarLong, not Int64.
    public void WriteVarLong(long number)
    {
        ulong value = unchecked((ulong)number);
        while ((value & ~0x7FUL) != 0)
        {
            _stream.WriteByte((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        _stream.WriteByte((byte)value);
    }

    public void WriteString(string value)
    {
        byte[] bytes = Protocol.EncodeUtf8(value);
        WriteVarInt(bytes.Length);
        _stream.Write(bytes);
    }

    public void WriteInt64(long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, value);
        _stream.Write(b);
    }

    public void WriteUuid(Guid uuid) => _stream.Write(uuid.ToByteArray(bigEndian: true));

    // Post-1.21.5 Text Component: an unnamed network-NBT String Tag (not JSON).
    public void WritePlainTextComponent(string text)
    {
        var bytes = Protocol.EncodeUtf8(text);
        if (bytes.Length > ushort.MaxValue)
            throw new InvalidDataException("NBT String Tag is too large.");
        WriteByte(8); // TAG_String
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)bytes.Length);
        _stream.Write(length);
        _stream.Write(bytes);
    }
}
