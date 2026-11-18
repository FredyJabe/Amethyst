namespace Amethyst;

public readonly record struct IncomingChat(string Message, bool HasSignature);
public readonly record struct IncomingCommand(string Command, bool HasSignedArguments);

// Version 777: text chat is received as a signed-capable client packet but
// deliberately rebroadcast as SYSTEM CHAT, not forged/rewritten signed player chat.
// This prototype does NOT validate chat signatures or claim secure chat support.
public static class ChatProtocol
{
    public const int ServerboundChatAck = 0x06;
    public const int ServerboundCommand = 0x07;
    public const int ServerboundSignedCommand = 0x08;
    public const int ServerboundChat = 0x09;
    public const int ServerboundSessionUpdate = 0x0A;
    public const int ClientboundCommands = 0x10;
    public const int ClientboundDisconnect = 0x20;
    public const int ClientboundSystemChat = 0x7C;
    private const int AckBitsetBytes = 3; // fixed bit set with 20 bits

    public static IncomingChat ParseChat(Packet packet)
    {
        if (packet.Id != ServerboundChat) throw new ArgumentException("Not a chat packet.");
        PacketReader r = packet.Reader();
        string message = r.ReadString(256);
        _ = r.ReadInt64(); // UTC timestamp, milliseconds
        _ = r.ReadInt64(); // salt
        bool signed = ReadOptionalSignature(r);
        ReadAcknowledgements(r);
        r.RequireEnd();
        return new IncomingChat(message, signed);
    }

    public static IncomingCommand ParseCommand(Packet packet)
    {
        if (packet.Id != ServerboundCommand && packet.Id != ServerboundSignedCommand)
            throw new ArgumentException("Not a command packet.");
        PacketReader r = packet.Reader();
        string command = r.ReadString(32767);
        bool signed = false;
        if (packet.Id == ServerboundSignedCommand)
        {
            _ = r.ReadInt64(); // timestamp
            _ = r.ReadInt64(); // salt
            int count = r.ReadVarInt();
            if (count is < 0 or > 8) throw new InvalidDataException("Invalid signed command argument count.");
            for (int i = 0; i < count; i++)
            {
                _ = r.ReadString(16);
                _ = r.ReadFixedBytes(256);
            }
            signed = count != 0;
            ReadAcknowledgements(r);
        }
        r.RequireEnd();
        return new IncomingCommand(command, signed);
    }

    public static void ParseAck(Packet packet)
    {
        var r = packet.Reader();
        int count = r.ReadVarInt();
        if (count is < 0 or > 65535) throw new InvalidDataException("Invalid message acknowledgement count.");
        r.RequireEnd();
    }

    public static void ParseSessionUpdate(Packet packet)
    {
        var r = packet.Reader();
        _ = r.ReadUuid();
        _ = r.ReadInt64();
        _ = r.ReadByteArray(512);
        _ = r.ReadByteArray(4096);
        r.RequireEnd();
    }

    private static bool ReadOptionalSignature(PacketReader r)
    {
        byte b = r.ReadByte();
        if (b is not (0 or 1)) throw new InvalidDataException("Invalid optional signature flag.");
        if (b == 1) _ = r.ReadFixedBytes(256);
        return b == 1;
    }

    private static void ReadAcknowledgements(PacketReader r)
    {
        int messageCount = r.ReadVarInt();
        if (messageCount is < 0 or > 65535) throw new InvalidDataException("Invalid chat message count.");
        _ = r.ReadFixedBytes(AckBitsetBytes);
        _ = r.ReadByte(); // rolling checksum
    }

    public static Task SendSystemAsync(PacketChannel channel, string message, CancellationToken ct) =>
        channel.SendAsync(ClientboundSystemChat, p =>
        {
            p.WritePlainTextComponent(message);
            p.WriteBool(false); // not an overlay/actionbar message
        }, ct);

    public static Task SendDisconnectAsync(PacketChannel channel, string reason, CancellationToken ct) =>
        channel.SendAsync(ClientboundDisconnect, p => p.WritePlainTextComponent(reason), ct);

    public static bool IsSafeMessage(string message, int maxLength) =>
        !string.IsNullOrWhiteSpace(message) && message.Length <= maxLength &&
        !message.Any(c => char.IsControl(c) || c == '\u00a7'); // no embedded newlines or legacy formatting escape
}
