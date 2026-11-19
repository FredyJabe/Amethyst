namespace Amethyst;

public readonly record struct PlayerCommandAction(
    int EntityId, int Action, int Data);

/// <summary>Java 26.3 (protocol 777) START_FALL_FLYING and entity flags.</summary>
public static class FlightProtocol
{
    public const int ServerboundPlayerCommand = 0x2A;
    public const int StartFallFlying = 6;
    public const int ClientboundSetEntityData = 0x65;
    public const int ClientboundSetEquipment = 0x68;

    public static PlayerCommandAction ReadPlayerCommand(Packet packet)
    {
        PacketReader reader = packet.Reader();
        int entity = reader.ReadVarInt();
        int action = reader.ReadVarInt();
        int data = reader.ReadVarInt();
        reader.RequireEnd();
        if (entity <= 0 || action is < 0 or > 6)
            throw new InvalidDataException("Invalid player command packet.");
        return new PlayerCommandAction(entity, action, data);
    }

    public static Task SendGlidingAsync(PacketChannel receiver,
        int entityId, bool gliding, CancellationToken ct) =>
        receiver.SendAsync(ClientboundSetEntityData, p =>
        {
            p.WriteVarInt(entityId);
            p.WriteByte(0); // base Entity shared flags
            p.WriteVarInt(0); // byte serializer
            p.WriteByte(gliding ? (byte)0x80 : (byte)0x00);
            p.WriteByte(0xFF);
        }, ct);

    public static Task SendElytraEquipmentAsync(PacketChannel observer,
        int entityId, ItemStack elytra, CancellationToken ct) =>
        observer.SendAsync(ClientboundSetEquipment, w =>
        {
            w.WriteVarInt(entityId);
            w.WriteByte(4); // chest equipment slot, last entry
            InventoryProtocol.WriteStack(w, elytra);
        }, ct);
}
