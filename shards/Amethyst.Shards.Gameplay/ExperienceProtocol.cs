namespace Amethyst;

/// <summary>Java 26.3 XP orb spawn, XP value metadata and pickup packets.
/// Experience orb type ID 50 and metadata slot 8 / VarInt serializer 1.
/// The player XP bar uses clientbound set_experience (0x69).</summary>
public static class ExperienceProtocol
{
    public const int OrbEntityType = 50;
    public const int SetExperience = 0x69;
    public const int ExperienceMetadataIndex = 8;
    public const int VarIntMetadataType = 1;

    public static async Task SendOrbAsync(PacketChannel channel,
        ExperienceOrb orb, CancellationToken ct)
    {
        await channel.SendAsync(DroppedItemProtocol.AddEntity, writer =>
        {
            writer.WriteVarInt(orb.EntityId);
            writer.WriteUuid(orb.Uuid);
            writer.WriteVarInt(OrbEntityType);
            writer.WriteDouble(orb.X);
            writer.WriteDouble(orb.Y);
            writer.WriteDouble(orb.Z);
            writer.WriteByte(0); // LP_VEC3 zero velocity
            writer.WriteByte(0); // pitch
            writer.WriteByte(0); // yaw
            writer.WriteByte(0); // head yaw
            writer.WriteVarInt(0); // entity-specific data
        }, ct);
        await channel.SendAsync(DroppedItemProtocol.SetEntityData, writer =>
        {
            writer.WriteVarInt(orb.EntityId);
            writer.WriteByte(ExperienceMetadataIndex);
            writer.WriteVarInt(VarIntMetadataType);
            writer.WriteVarInt(orb.Value);
            writer.WriteByte(0xFF);
        }, ct);
    }

    public static Task SendPositionAsync(PacketChannel channel,
        ExperienceOrb orb, CancellationToken ct) =>
        channel.SendAsync(DroppedItemProtocol.TeleportEntity, writer =>
        {
            writer.WriteVarInt(orb.EntityId);
            writer.WriteDouble(orb.X);
            writer.WriteDouble(orb.Y);
            writer.WriteDouble(orb.Z);
            writer.WriteDouble(0);
            writer.WriteDouble(orb.VelocityY);
            writer.WriteDouble(0);
            writer.WriteFloat(0);
            writer.WriteFloat(0);
            writer.WriteInt32(0);
            writer.WriteBool(orb.OnGround);
        }, ct);

    public static Task SendPickupAsync(PacketChannel channel,
        ExperienceOrb orb, ConnectedPlayer player, CancellationToken ct) =>
        DroppedItemProtocol.SendPickupAsync(channel,
            orb.EntityId, player.EntityId, 1, ct);

    public static Task SendExperienceAsync(PacketChannel channel,
        PlayerExperience experience, CancellationToken ct)
    {
        (float progress, int level, int total) = experience.Snapshot();
        return channel.SendAsync(SetExperience, writer =>
        {
            writer.WriteFloat(progress);
            writer.WriteVarInt(level);
            writer.WriteVarInt(total);
        }, ct);
    }
}
