namespace Amethyst;

/// <summary>Java 26.3 projectile entity types and 20Hz movement updates.
/// All data comes from authoritative server simulations, never client
/// positions or client-claimed entity hit coordinates.</summary>
public static class ProjectileProtocol
{
    public const int ArrowEntityType = 6;
    public const int SpectralArrowType = 126;
    public const int TridentEntityType = 139;
    public const int SnowballEntityType = 123;
    public const int EggEntityType = 40;
    public const int EnderPearlEntityType = 45;
    public const int SplashPotionEntityType = 108;

    public static int EntityType(ProjectileKind kind) => kind switch
    {
        ProjectileKind.Arrow => ArrowEntityType,
        ProjectileKind.SpectralArrow => SpectralArrowType,
        ProjectileKind.Trident => TridentEntityType,
        ProjectileKind.Snowball => SnowballEntityType,
        ProjectileKind.Egg => EggEntityType,
        ProjectileKind.EnderPearl => EnderPearlEntityType,
        ProjectileKind.SplashPotion => SplashPotionEntityType,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static async Task SendSpawnAsync(PacketChannel channel,
        ProjectileEntity projectile, CancellationToken ct)
    {
        await channel.SendAsync(0x01, w =>
        {
            w.WriteVarInt(projectile.EntityId);
            w.WriteUuid(projectile.Uuid);
            w.WriteVarInt(EntityType(projectile.Kind));
            w.WriteDouble(projectile.X); w.WriteDouble(projectile.Y);
            w.WriteDouble(projectile.Z);
            w.WriteByte(0); // zero LP_VEC3; 20Hz teleports follow
            w.WriteByte(0); w.WriteByte(0); w.WriteByte(0);
            w.WriteVarInt(0); // additional owner metadata
        }, ct);
        await SendPositionAsync(channel, projectile, ct);
    }

    public static Task SendPositionAsync(PacketChannel channel,
        ProjectileEntity p, CancellationToken ct) =>
        channel.SendAsync(DroppedItemProtocol.TeleportEntity, w =>
        {
            w.WriteVarInt(p.EntityId);
            w.WriteDouble(p.X); w.WriteDouble(p.Y); w.WriteDouble(p.Z);
            w.WriteDouble(p.Vx); w.WriteDouble(p.Vy); w.WriteDouble(p.Vz);
            w.WriteFloat(0); w.WriteFloat(0);
            w.WriteInt32(0);
            w.WriteBool(false);
        }, ct);

    public static Task SendRemoveAsync(PacketChannel channel,
        ProjectileEntity projectile, CancellationToken ct) =>
        DroppedItemProtocol.SendRemoveAsync(channel, projectile.EntityId, ct);
}
