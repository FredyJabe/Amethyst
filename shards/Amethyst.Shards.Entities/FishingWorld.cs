using Amethyst.Shards;

using Amethyst;

namespace Amethyst.BuiltinShards;

/// <summary>Server-authoritative one-bobber-per-player fishing state.
/// Casts have physical trajectories and bite windows. Reeling only
/// awards loot when the bobber is biting in water.</summary>
public sealed class FishingWorld : IShardFishingWorld
{
    private readonly object _gate = new();
    private readonly Func<int> _allocate;
    private readonly Dictionary<Guid, FishingBobber> _hooks = new();
    public const int MaximumAgeTicks = 20 * 120;

    public FishingWorld(Func<int> allocateId) => _allocate = allocateId;
    public bool CheckOpenWater(IShardBlockWorld world, FishingBobber bobber) =>
        IsOpenWater(world, bobber);

    public (string Item, int Xp) RollStarterLoot(Random? random = null) =>
        RollLoot(random);


    public FishingBobber[] Snapshot()
    {
        lock (_gate) return _hooks.Values.OrderBy(x => x.EntityId).ToArray();
    }

    public bool HasHook(Guid owner)
    {
        lock (_gate) return _hooks.ContainsKey(owner);
    }

    public bool TryCast(Guid owner, int ownerEntityId, PlayerPose pos,
        out FishingBobber bobber, int waitReductionSeconds = 0)
    {
        bobber = default;
        lock (_gate)
        {
            if (_hooks.ContainsKey(owner) || _hooks.Count >= 512) return false;
            double yaw = pos.Yaw * Math.PI / 180,
                pitch = pos.Pitch * Math.PI / 180;
            double forwardX = -Math.Sin(yaw) * Math.Cos(pitch),
                forwardZ = Math.Cos(yaw) * Math.Cos(pitch),
                forwardY = -Math.Sin(pitch);
            bobber = new FishingBobber(_allocate(), Guid.NewGuid(),
                owner, ownerEntityId, pos.X + forwardX * 0.5,
                pos.Y + 1.45, pos.Z + forwardZ * 0.5,
                forwardX * 0.65, forwardY * 0.65 + 0.05,
                forwardZ * 0.65, false, false, 0, 0, 0)
                { WaitReductionSeconds = Math.Clamp(waitReductionSeconds, 0, 30) };
            _hooks.Add(owner, bobber);
            return true;
        }
    }

    public bool TryReel(Guid owner, out FishingReel result,
        long? nowMilliseconds = null)
    {
        result = default;
        lock (_gate)
        {
            if (!_hooks.Remove(owner, out FishingBobber hook)) return false;
            long now = nowMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool caught = hook.InWater && hook.Biting &&
                hook.BiteUntilMs >= now;
            result = new FishingReel(caught, hook.InWater, hook);
            return true;
        }
    }

    public bool Cancel(Guid owner)
    {
        lock (_gate) return _hooks.Remove(owner);
    }

    public FishingBobber[] Tick(IShardBlockWorld world,
        long? nowMilliseconds = null, Random? random = null)
    {
        long now = nowMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        random ??= Random.Shared;
        lock (_gate)
        {
            foreach (var original in _hooks.Values.ToArray())
            {
                if (original.AgeTicks >= MaximumAgeTicks)
                {
                    _hooks.Remove(original.Owner);
                    continue;
                }
                FishingBobber bobber = original;
                int bx = (int)Math.Floor(bobber.X),
                    by = (int)Math.Floor(bobber.Y),
                    bz = (int)Math.Floor(bobber.Z);
                ushort state = by >= -64 && by <= 319 ?
                    world.GetBlockState(bx, by, bz) : (ushort)0;
                bool inWater = state is >= 89 and <= 104;
                if (inWater)
                {
                    long biteAt = bobber.BiteAtMs;
                    long biteUntil = bobber.BiteUntilMs;
                    bool biting = bobber.Biting;
                    if (biteAt == 0) biteAt = now + Math.Max(1000,
                        random.Next(5000, 16001) - bobber.WaitReductionSeconds * 1000);
                    if (now >= biteAt && !biting)
                    {
                        biting = true;
                        biteUntil = now + 2500;
                    }
                    if (biting && now > biteUntil)
                    {
                        biting = false;
                        biteAt = now + Math.Max(1000,
                            random.Next(5000, 16001) - bobber.WaitReductionSeconds * 1000);
                    }
                    bobber = bobber with
                    {
                        InWater = true, Biting = biting,
                        BiteAtMs = biteAt, BiteUntilMs = biteUntil,
                        Vx = 0, Vy = 0, Vz = 0,
                        Y = by + 0.82
                    };
                }
                else
                {
                    double nextX = bobber.X + bobber.Vx,
                        nextY = bobber.Y + bobber.Vy,
                        nextZ = bobber.Z + bobber.Vz;
                    if (nextY < -64 || nextY > 320)
                    {
                        _hooks.Remove(original.Owner);
                        continue;
                    }
                    int nx = (int)Math.Floor(nextX), ny = (int)Math.Floor(nextY),
                        nz = (int)Math.Floor(nextZ);
                    ushort nextBlock = world.GetBlockState(nx, ny, nz);
                    if (nextBlock is >= 89 and <= 104)
                    {
                        bobber = bobber with
                        {
                            X = nextX, Y = nextY, Z = nextZ,
                            Vx = 0, Vy = 0, Vz = 0
                        };
                    }
                    else if (nextBlock != 0)
                        bobber = bobber with { Vx = 0, Vy = 0, Vz = 0 };
                    else
                        bobber = bobber with
                        {
                            X = nextX, Y = nextY, Z = nextZ,
                            Vx = bobber.Vx * 0.92,
                            Vy = bobber.Vy * 0.92 - 0.03,
                            Vz = bobber.Vz * 0.92
                        };
                }
                _hooks[original.Owner] = bobber with
                {
                    AgeTicks = original.AgeTicks + 1
                };
            }
            return _hooks.Values.OrderBy(x => x.EntityId).ToArray();
        }
    }

    /// <summary>Conservative 5x5 open-water check for the native fishing
    /// table's treasure predicate. A complete vanilla check also accounts
    /// for fluid-state properties and allowed surface vegetation.</summary>
    public static bool IsOpenWater(IShardBlockWorld world, FishingBobber bobber)
    {
        int centerX = (int)Math.Floor(bobber.X);
        int centerY = (int)Math.Floor(bobber.Y);
        int centerZ = (int)Math.Floor(bobber.Z);
        if (centerY is < -62 or > 317) return false;
        for (int x = centerX - 2; x <= centerX + 2; x++)
        for (int z = centerZ - 2; z <= centerZ + 2; z++)
        {
            for (int y = centerY - 1; y <= centerY; y++)
                if (world.GetBlockState(x, y, z) is < 89 or > 104)
                    return false;
            for (int y = centerY + 1; y <= centerY + 2; y++)
                if (world.GetBlockState(x, y, z) != 0)
                    return false;
        }
        return true;
    }

    public static (string Item, int Xp) RollLoot(Random? random = null)
    {
        random ??= Random.Shared;
        int roll = random.Next(100);
        string item = roll switch
        {
            < 55 => "minecraft:cod",
            < 80 => "minecraft:salmon",
            < 89 => "minecraft:pufferfish",
            < 95 => "minecraft:tropical_fish",
            < 98 => "minecraft:bowl",
            _ => "minecraft:nautilus_shell"
        };
        return (item, random.Next(1, 7));
    }
}
