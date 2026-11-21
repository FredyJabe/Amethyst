using Amethyst.Shards;

using Amethyst;

namespace Amethyst.BuiltinShards;

/// <summary>Supported physical projectile families for Java 26.3.</summary>
/// <summary>20Hz server-authoritative projectiles. Trajectories are ray-marched
/// to avoid tunneling, with collision against blocks, players and livestock.
/// Projectiles are transient; recoverable arrows/tridents become normal
/// persistent ground items when they land.</summary>
public sealed class ProjectileWorld : IShardProjectileWorld
{
    private readonly object _gate = new();
    private readonly Func<int> _allocate;
    private readonly Dictionary<int, ProjectileEntity> _entities = new();
    public const int MaximumProjectiles = 1024;

    public ProjectileWorld(Func<int> allocateId) => _allocate = allocateId;

    public ProjectileEntity[] Snapshot()
    {
        lock (_gate) return _entities.Values.OrderBy(x => x.EntityId).ToArray();
    }

    public bool TryLaunch(Guid owner, ProjectileKind kind,
        PlayerPose source, double speed, float damage,
        ItemStack recoverable, out ProjectileEntity projectile,
        ItemStack payload = default)
    {
        projectile = default;
        if (!double.IsFinite(speed) || speed <= 0 || speed > 6 ||
            !float.IsFinite(damage) || damage < 0 ||
            !double.IsFinite(source.X) || !double.IsFinite(source.Y) ||
            !double.IsFinite(source.Z))
            return false;
        lock (_gate)
        {
            if (_entities.Count >= MaximumProjectiles) return false;
            double yaw = source.Yaw * Math.PI / 180,
                pitch = source.Pitch * Math.PI / 180;
            double vx = -Math.Sin(yaw) * Math.Cos(pitch),
                vy = -Math.Sin(pitch), vz = Math.Cos(yaw) * Math.Cos(pitch);
            // Launch slightly ahead of the player's eyes so the projectile
            // does not collide with the shooter on the first physics step.
            projectile = new ProjectileEntity(_allocate(), Guid.NewGuid(), owner,
                kind, source.X + vx * 0.55, source.Y + 1.52 + vy * 0.25,
                source.Z + vz * 0.55, vx * speed, vy * speed,
                vz * speed, damage, 0, recoverable)
                { Payload = payload };
            _entities.Add(projectile.EntityId, projectile);
            return true;
        }
    }

    public ProjectileFrame Tick(IShardBlockWorld world,
        IReadOnlyList<ProjectilePlayer> players,
        IReadOnlyList<ShardEntityInstance> mobs)
    {
        var moving = new List<ProjectileEntity>();
        var impacts = new List<ProjectileImpact>();
        lock (_gate)
        {
            foreach (ProjectileEntity old in _entities.Values.ToArray())
            {
                double x = old.X, y = old.Y, z = old.Z;
                Guid playerHit = Guid.Empty;
                int mobHit = 0;
                bool blockHit = false;
                // Sample each 0.2 blocks along the segment, including high
                // velocity arrows and thrown tridents.
                double distance = Math.Sqrt(old.Vx * old.Vx +
                    old.Vy * old.Vy + old.Vz * old.Vz);
                int steps = Math.Clamp((int)Math.Ceiling(distance / 0.2), 1, 64);
                for (int step = 1; step <= steps; step++)
                {
                    double fraction = (double)step / steps;
                    x = old.X + old.Vx * fraction;
                    y = old.Y + old.Vy * fraction;
                    z = old.Z + old.Vz * fraction;
                    if (!double.IsFinite(x) || !double.IsFinite(y) ||
                        !double.IsFinite(z) || y < -65 || y > 321)
                    {
                        blockHit = true;
                        break;
                    }
                    ushort state = world.GetBlockState((int)Math.Floor(x),
                        (int)Math.Floor(y), (int)Math.Floor(z));
                    // Water is not a solid projectile target.
                    if (state != 0 && state is not (>= 89 and <= 104))
                    {
                        blockHit = true;
                        break;
                    }
                    foreach (ProjectilePlayer candidate in players)
                    {
                        if (candidate.PlayerUuid == old.Owner) continue;
                        PlayerPose p = candidate.Pose;
                        double dx = p.X - x, dz = p.Z - z;
                        if (dx * dx + dz * dz < 0.45 * 0.45 &&
                            y >= p.Y && y <= p.Y + 1.8)
                        {
                            playerHit = candidate.PlayerUuid;
                            break;
                        }
                    }
                    if (playerHit != Guid.Empty) break;
                    foreach (ShardEntityInstance mob in mobs)
                    {
                        double dx = mob.X - x, dz = mob.Z - z;
                        if (dx * dx + dz * dz < 0.6 * 0.6 &&
                            y >= mob.Y && y <= mob.Y + 1.4)
                        {
                            mobHit = mob.EntityId;
                            break;
                        }
                    }
                    if (mobHit != 0) break;
                }
                _entities.Remove(old.EntityId);
                if (blockHit || playerHit != Guid.Empty || mobHit != 0 ||
                    old.AgeTicks >= 20 * 60)
                {
                    impacts.Add(new ProjectileImpact(old with
                    {
                        X = x, Y = y, Z = z
                    }, playerHit, mobHit, blockHit));
                    continue;
                }
                double gravity = old.Kind is ProjectileKind.Arrow or
                    ProjectileKind.SpectralArrow or ProjectileKind.Trident ? 0.05 :
                    0.03;
                double drag = 0.99;
                var next = old with
                {
                    X = x, Y = y, Z = z,
                    Vx = old.Vx * drag,
                    Vy = old.Vy * drag - gravity,
                    Vz = old.Vz * drag,
                    AgeTicks = old.AgeTicks + 1
                };
                _entities.Add(next.EntityId, next);
                moving.Add(next);
            }
        }
        return new ProjectileFrame(moving.ToArray(), impacts.ToArray());
    }
}
