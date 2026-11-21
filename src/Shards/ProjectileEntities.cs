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
    public bool CanRecover => Recoverable.Count > 0;
}

public readonly record struct ProjectileImpact(ProjectileEntity Projectile,
    Guid PlayerHit, int MobHit, bool HitBlock);

public readonly record struct ProjectileFrame(ProjectileEntity[] Moving,
    ProjectileImpact[] Impacts);


/// <summary>Server-validated collision candidate, without Gameplay-owned
/// sessions or network connections.</summary>
public readonly record struct ProjectilePlayer(Guid PlayerUuid, PlayerPose Pose);
