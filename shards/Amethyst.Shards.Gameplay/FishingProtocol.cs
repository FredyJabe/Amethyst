namespace Amethyst;

/// <summary>Java 26.3 entity type 160 is the fishing bobber. Metadata
/// index 9 marks a fish biting; entity data tracks the owner.</summary>
public static class FishingProtocol
{
    public const int BobberEntityType = 160;

    public static async Task SendSpawnAsync(PacketChannel channel,
        FishingBobber hook, CancellationToken ct)
    {
        await channel.SendAsync(DroppedItemProtocol.AddEntity, p =>
        {
            p.WriteVarInt(hook.EntityId);
            p.WriteUuid(hook.Uuid);
            p.WriteVarInt(BobberEntityType);
            p.WriteDouble(hook.X); p.WriteDouble(hook.Y); p.WriteDouble(hook.Z);
            p.WriteByte(0); // zero LP_VEC3
            p.WriteByte(0); p.WriteByte(0); p.WriteByte(0);
            p.WriteVarInt(hook.OwnerEntityId);
        }, ct);
        await SendMetadataAsync(channel, hook, ct);
    }

    public static Task SendMetadataAsync(PacketChannel channel,
        FishingBobber hook, CancellationToken ct) =>
        channel.SendAsync(DroppedItemProtocol.SetEntityData, p =>
        {
            p.WriteVarInt(hook.EntityId);
            p.WriteByte(8); // hooked entity ID
            p.WriteVarInt(1); // VAR_INT serializer
            p.WriteVarInt(0); // none
            p.WriteByte(9); // biting
            p.WriteVarInt(8); // BOOL serializer
            p.WriteBool(hook.Biting);
            p.WriteByte(0xFF);
        }, ct);

    public static Task SendPositionAsync(PacketChannel channel,
        FishingBobber hook, CancellationToken ct) =>
        channel.SendAsync(DroppedItemProtocol.TeleportEntity, p =>
        {
            p.WriteVarInt(hook.EntityId);
            p.WriteDouble(hook.X); p.WriteDouble(hook.Y); p.WriteDouble(hook.Z);
            p.WriteDouble(hook.Vx); p.WriteDouble(hook.Vy); p.WriteDouble(hook.Vz);
            p.WriteFloat(0); p.WriteFloat(0);
            p.WriteInt32(0);
            p.WriteBool(hook.InWater);
        }, ct);

    public static Task SendRemoveAsync(PacketChannel channel,
        FishingBobber hook, CancellationToken ct) =>
        DroppedItemProtocol.SendRemoveAsync(channel, hook.EntityId, ct);
}
