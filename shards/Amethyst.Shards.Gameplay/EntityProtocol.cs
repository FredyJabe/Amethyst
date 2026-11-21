using Amethyst.Shards;

namespace Amethyst;

/// <summary>Java 26.3 (protocol 777) clientbound packets for ordinary entity
/// types. Spawning excludes player/item/special-purpose entities until their
/// required metadata, equipment and behavior are implemented separately.</summary>
public static class EntityProtocol
{
    public static Task SendSpawnAsync(PacketChannel channel, ShardEntityInstance entity,
        CancellationToken ct) =>
        channel.SendAsync(0x01, writer =>
        {
            writer.WriteVarInt(entity.EntityId);
            writer.WriteUuid(entity.Uuid);
            writer.WriteVarInt(entity.ProtocolId);
            writer.WriteDouble(entity.X);
            writer.WriteDouble(entity.Y);
            writer.WriteDouble(entity.Z);
            writer.WriteByte(0); // zero LP_VEC3 movement
            writer.WriteByte(ToAngle(entity.Pitch));
            writer.WriteByte(ToAngle(entity.Yaw));
            writer.WriteByte(ToAngle(entity.Yaw)); // head yaw
            writer.WriteVarInt(0); // entity-specific data
        }, ct);

    public static Task SendTeleportAsync(PacketChannel channel, ShardEntityInstance entity,
        CancellationToken ct) =>
        channel.SendAsync(0x80, writer =>
        {
            writer.WriteVarInt(entity.EntityId);
            writer.WriteDouble(entity.X);
            writer.WriteDouble(entity.Y);
            writer.WriteDouble(entity.Z);
            writer.WriteDouble(0); // velocity X
            writer.WriteDouble(0); // velocity Y
            writer.WriteDouble(0); // velocity Z
            writer.WriteFloat(entity.Yaw);
            writer.WriteFloat(entity.Pitch);
            writer.WriteInt32(0); // absolute position/rotation
            writer.WriteBool(true); // static entity on ground (no physics yet)
        }, ct);

    public static Task SendRemoveAsync(PacketChannel channel, int entityId,
        CancellationToken ct) =>
        channel.SendAsync(0x4E, writer =>
        {
            writer.WriteVarInt(1);
            writer.WriteVarInt(entityId);
        }, ct);

    private static byte ToAngle(float degrees) =>
        unchecked((byte)(int)(degrees * (256.0 / 360.0)));
}
