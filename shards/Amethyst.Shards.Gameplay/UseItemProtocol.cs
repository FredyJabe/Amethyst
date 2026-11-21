namespace Amethyst;

/// <summary>Java 26.3 (protocol 777) serverbound Use Item and player-use
/// metadata. Packet contents are validated before acting on inventory.</summary>
public readonly record struct UseItemAction(int Hand, int Sequence,
    float Yaw, float Pitch);

public static class UseItemProtocol
{
    public const int ServerboundUseItem = 0x43;
    public const int ClientboundSetEntityData = 0x65;

    public static UseItemAction ReadUseItem(Packet packet)
    {
        var reader = packet.Reader();
        int hand = reader.ReadVarInt(), sequence = reader.ReadVarInt();
        float yaw = reader.ReadFloat(), pitch = reader.ReadFloat();
        reader.RequireEnd();
        if (hand is < 0 or > 1 || sequence < 0 ||
            !float.IsFinite(yaw) || !float.IsFinite(pitch))
            throw new InvalidDataException("Invalid use item packet.");
        return new UseItemAction(hand, sequence, yaw, pitch);
    }

    public static Task SendShieldMetadataAsync(PacketChannel connection,
        int entityId, int hand, CancellationToken ct) =>
        connection.SendAsync(ClientboundSetEntityData, p =>
        {
            p.WriteVarInt(entityId);
            p.WriteByte(8); // living entity data flags
            p.WriteVarInt(0); // byte data serializer
            p.WriteByte(hand < 0 ? (byte)0 :
                hand == 1 ? (byte)0x03 : (byte)0x01);
            p.WriteByte(0xFF);
        }, ct);
}
