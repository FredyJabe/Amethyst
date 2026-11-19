using Amethyst.Shards;

namespace Amethyst;

/// <summary>Java Edition 26.3 / protocol 777 dropped item entity packets.
/// IDs, entity type 72, DATA_ITEM index 8, and ITEM_STACK serializer 7
/// are pinned to Minecraft 26.3 Mojang wire/registry data.</summary>
public static class DroppedItemProtocol
{
    public const int ItemEntityType = 72;
    public const int StackMetadataIndex = 8;
    public const int StackMetadataSerializer = 7;
    public const int AddEntity = 0x01;
    public const int RemoveEntities = 0x4E;
    public const int SetEntityData = 0x65;
    public const int TakeItemEntity = 0x7F;
    public const int TeleportEntity = 0x80;

    public static async Task SendSpawnAsync(PacketChannel channel,
        GroundItem item, CancellationToken ct,
        IShardItemRegistry? items = null)
    {
        await channel.SendAsync(AddEntity, writer =>
        {
            writer.WriteVarInt(item.EntityId);
            writer.WriteUuid(item.Uuid);
            writer.WriteVarInt(ItemEntityType);
            writer.WriteDouble(item.X);
            writer.WriteDouble(item.Y);
            writer.WriteDouble(item.Z);
            writer.WriteByte(0); // LP_VEC3: zero movement
            writer.WriteByte(0); // pitch
            writer.WriteByte(0); // yaw
            writer.WriteByte(0); // head yaw
            writer.WriteVarInt(0); // entity-specific data
        }, ct);
        await SendStackAsync(channel, item, ct, items);
    }

    public static Task SendStackAsync(PacketChannel channel,
        GroundItem item, CancellationToken ct,
        IShardItemRegistry? items = null) =>
        channel.SendAsync(SetEntityData, writer =>
        {
            writer.WriteVarInt(item.EntityId);
            writer.WriteByte(StackMetadataIndex);
            writer.WriteVarInt(StackMetadataSerializer);
            InventoryProtocol.WriteStack(writer, item.ResolvedStack, items);
            writer.WriteByte(0xFF); // end of metadata list
        }, ct);

    public static Task SendPositionAsync(PacketChannel channel,
        GroundItem item, CancellationToken ct) =>
        channel.SendAsync(TeleportEntity, writer =>
        {
            writer.WriteVarInt(item.EntityId);
            writer.WriteDouble(item.X);
            writer.WriteDouble(item.Y);
            writer.WriteDouble(item.Z);
            writer.WriteDouble(0); // delta movement X
            writer.WriteDouble(item.VelocityY); // delta movement Y
            writer.WriteDouble(0); // delta movement Z
            writer.WriteFloat(0); // yRot
            writer.WriteFloat(0); // xRot
            writer.WriteInt32(0); // no relative position/rotation flags
            writer.WriteBool(item.OnGround);
        }, ct);

    public static Task SendPickupAsync(PacketChannel channel,
        int itemEntityId, int collectorEntityId, int amount, CancellationToken ct) =>
        channel.SendAsync(TakeItemEntity, writer =>
        {
            writer.WriteVarInt(itemEntityId);
            writer.WriteVarInt(collectorEntityId);
            writer.WriteVarInt(amount);
        }, ct);

    public static Task SendRemoveAsync(PacketChannel channel,
        int entityId, CancellationToken ct) =>
        channel.SendAsync(RemoveEntities, writer =>
        {
            writer.WriteVarInt(1);
            writer.WriteVarInt(entityId);
        }, ct);
}
