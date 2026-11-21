using Amethyst;

namespace Amethyst.Shards;

/// <summary>Physical projectile state and collision simulation, owned by the
/// Entities Shard. Gameplay supplies validated player positions and handles
/// combat effects and packet serialization.</summary>
public interface IShardProjectileWorld
{
    ProjectileEntity[] Snapshot();
    bool TryLaunch(Guid owner, ProjectileKind kind, PlayerPose source,
        double speed, float damage, ItemStack recoverable,
        out ProjectileEntity projectile, ItemStack payload = default,
        ItemStack sourceWeapon = default);
    ProjectileFrame Tick(IShardBlockWorld world,
        IReadOnlyList<ProjectilePlayer> players,
        IReadOnlyList<ShardEntityInstance> mobs);
}
