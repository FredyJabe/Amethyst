namespace Amethyst;

/// <summary>Java Edition 26.3 protocol 777 entity attack and health packets.
/// Damage and death are decided entirely by the server.</summary>
public static class CombatProtocol
{
    public const int ServerboundAttack = 0x01;
    public const int ClientboundHealth = 0x6A;
    public const int ClientboundDamageEvent = 0x19;
    public const int ClientboundHurtAnimation = 0x2B;
    public const int ClientboundSetMotion = 0x67;
    public const int ClientboundEntityEvent = 0x22;
    public const int ClientboundRespawn = 0x54;

    public static int ReadAttack(Packet packet)
    {
        var reader = packet.Reader();
        int entityId = reader.ReadVarInt();
        reader.RequireEnd();
        if (entityId < 0) throw new InvalidDataException("Invalid attack entity.");
        return entityId;
    }

    public static Task SendHealthAsync(ConnectedPlayer target, CancellationToken ct)
    {
        var (hp, food, saturation) = target.Vitals.Snapshot();
        return target.Connection.SendAsync(ClientboundHealth, p =>
        {
            p.WriteFloat(hp); p.WriteVarInt(food); p.WriteFloat(saturation);
        }, ct);
    }

    public static Task SendHurtAsync(PacketChannel observer, int targetId,
        CancellationToken ct) =>
        observer.SendAsync(ClientboundHurtAnimation, p =>
        {
            p.WriteVarInt(targetId);
            p.WriteFloat(0);
        }, ct);

    public static Task SendDeathAsync(PacketChannel target, int entityId,
        CancellationToken ct) =>
        target.SendAsync(ClientboundEntityEvent, p =>
        {
            p.WriteInt32(entityId); p.WriteByte(3); // death animation
        }, ct);

    public static Task SendMotionAsync(PacketChannel target, int entityId,
        double vx, double vy, double vz, CancellationToken ct) =>
        target.SendAsync(ClientboundSetMotion, p =>
        {
            p.WriteVarInt(entityId);
            p.WriteLpVec3(vx, vy, vz); // 26.3 compressed LP_VEC3
        }, ct);

    public static Task SendRespawnAsync(ConnectedPlayer player,
        Amethyst.Shards.IShardWorld world, CancellationToken ct) =>
        player.Connection.SendAsync(ClientboundRespawn, p =>
        {
            p.WriteVarInt(VanillaRegistries.IndexOf(
                "minecraft:dimension_type", world.DimensionId));
            p.WriteString(world.DimensionId);
            p.WriteInt64(world.Seed);
            p.WriteVarInt((int)player.GameMode);
            p.WriteVarInt(0);
            p.WriteBool(false); p.WriteBool(false);
            p.WriteBool(false);
            p.WriteVarInt(0);
            p.WriteVarInt(world.SeaLevel);
            p.WriteByte(0);
        }, ct);
}
