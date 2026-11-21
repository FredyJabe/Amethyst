namespace Amethyst;

public enum ProjectileKind : byte
{
    Arrow, SpectralArrow, Trident, Snowball, Egg, EnderPearl, SplashPotion
}

public readonly record struct ProjectileEntity(
    int EntityId, Guid Uuid, Guid Owner, ProjectileKind Kind,
    double X, double Y, double Z, double Vx, double Vy, double Vz,
    float Damage, int AgeTicks, ItemStack Recoverable)
{
    /// <summary>Full server-validated potion stack used by throwable item
    /// effects. This is not a recoverable projectile drop.</summary>
    public ItemStack Payload { get; init; }
    /// <summary>Authoritative firing/throwing weapon snapshot for
    /// ~onProjectileHit and ranged-loot enchantment calculations.</summary>
    public ItemStack SourceWeapon { get; init; }
    public bool CanRecover => Recoverable.Count > 0 &&
        Kind is ProjectileKind.Arrow or ProjectileKind.SpectralArrow or
            ProjectileKind.Trident;
}

public readonly record struct ProjectileImpact(ProjectileEntity Projectile,
    Guid PlayerHit, int MobHit, bool HitBlock);

public readonly record struct ProjectileFrame(ProjectileEntity[] Moving,
    ProjectileImpact[] Impacts);


/// <summary>Server-validated collision candidate, without Gameplay-owned
/// sessions or network connections.</summary>
public readonly record struct ProjectilePlayer(Guid PlayerUuid, PlayerPose Pose);
