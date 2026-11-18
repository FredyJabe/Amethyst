namespace Amethyst;

public readonly record struct PlayerBlockAction(int Status, int X, int Y, int Z,
    int Face, int Sequence);

public readonly record struct BlockUseOn(int Hand, int X, int Y, int Z,
    int Face, int Sequence, bool ValidHit);

public readonly record struct CreativeSlotUpdate(int Slot, int ItemId, bool PlainItem);

/// <summary>Java 26.3 (protocol 777) Creative block interaction packets.</summary>
public static class BlockInteractionProtocol
{
    public const int ServerboundPlayerAction = 0x29;
    public const int ServerboundSetHeldItem = 0x36;
    public const int ServerboundCreativeSlot = 0x39;
    public const int ServerboundUseItemOn = 0x42;
    public const int ClientboundChangedAck = 0x04;
    public const int ClientboundBlockUpdate = 0x08;

    public static PlayerBlockAction ReadAction(Packet packet)
    {
        var reader = packet.Reader();
        int status = reader.ReadVarInt();
        var (x, y, z) = reader.ReadPosition();
        byte face = reader.ReadByte();
        int sequence = reader.ReadVarInt();
        reader.RequireEnd();
        return new PlayerBlockAction(status, x, y, z, face, sequence);
    }

    public static BlockUseOn ReadUseOn(Packet packet)
    {
        var reader = packet.Reader();
        int hand = reader.ReadVarInt();
        var (x, y, z) = reader.ReadPosition();
        int face = reader.ReadVarInt();
        float hitX = reader.ReadFloat(), hitY = reader.ReadFloat(), hitZ = reader.ReadFloat();
        bool inside = reader.ReadByte() != 0;
        bool borderHit = reader.ReadByte() != 0;
        int sequence = reader.ReadVarInt();
        reader.RequireEnd();
        bool valid = (hand == 0 || hand == 1) && face is >= 0 and <= 5 &&
            float.IsFinite(hitX) && float.IsFinite(hitY) && float.IsFinite(hitZ) &&
            hitX is >= 0 and <= 1 && hitY is >= 0 and <= 1 && hitZ is >= 0 and <= 1 &&
            !inside && !borderHit;
        return new BlockUseOn(hand, x, y, z, face, sequence, valid);
    }

    public static CreativeSlotUpdate ReadCreativeSlot(Packet packet)
    {
        var reader = packet.Reader();
        int slot = reader.ReadInt16();
        int count = reader.ReadVarInt();
        if (count == 0)
        {
            reader.RequireEnd();
            return new CreativeSlotUpdate(slot, 0, true);
        }
        if (count is < 0 or > 64)
            return new CreativeSlotUpdate(slot, 0, false);
        int itemId = reader.ReadVarInt();
        int added = reader.ReadVarInt(), removed = reader.ReadVarInt();
        // Full data component decoding belongs in the inventory milestone.
        // Never accept an unparsed patch as a trusted Creative placement item.
        bool plain = added == 0 && removed == 0;
        if (plain) reader.RequireEnd();
        return new CreativeSlotUpdate(slot, itemId, plain);
    }

    public static int ReadHeldItem(Packet packet)
    {
        var reader = packet.Reader();
        int slot = reader.ReadInt16();
        reader.RequireEnd();
        return slot;
    }

    public static Task SendBlockUpdateAsync(PacketChannel connection,
        int x, int y, int z, ushort state, CancellationToken ct) =>
        connection.SendAsync(ClientboundBlockUpdate, writer =>
        {
            writer.WritePosition(x, y, z);
            writer.WriteVarInt(state);
        }, ct);

    public static Task SendAckAsync(PacketChannel connection, int sequence, CancellationToken ct) =>
        connection.SendAsync(ClientboundChangedAck, writer => writer.WriteVarInt(sequence), ct);

    public static (int X, int Y, int Z) Adjacent(int x, int y, int z, int face) => face switch
    {
        0 => (x, y - 1, z),
        1 => (x, y + 1, z),
        2 => (x, y, z - 1),
        3 => (x, y, z + 1),
        4 => (x - 1, y, z),
        5 => (x + 1, y, z),
        _ => (x, y, z)
    };
}
