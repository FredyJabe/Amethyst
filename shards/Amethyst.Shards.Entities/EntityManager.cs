using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>Thread-safe authoritative instance store. No automatic spawn,
/// AI or physics: other Shards can implement those via the typed service.</summary>
public sealed class EntityManager : IShardEntityManager, IShardEntityInteractions,
    IShardLivingEntities, IShardExperienceOrbs, IShardFishingWorld,
    IShardGroundItems, IShardProjectileWorld
{
    private const int MaximumEntities = 10_000;
    private const double MaximumCoordinate = 30_000_000;
    private readonly IShardEntityRegistry _definitions;
    private readonly Dictionary<int, ShardEntityInstance> _instances = new();
    private readonly Queue<ShardEntityInstance> _deaths = new();
    private readonly HashSet<int> _sheared = [];
    private readonly object _gate = new();
    private int _nextEntityId;
    private LivingEntityStore _livestock;
    private ExperienceOrbStore _experienceOrbs;
    private GroundItemStore _groundItems;
    private readonly FishingWorld _fishing;
    private readonly ProjectileWorld _projectiles;
    private bool _worldBound;

    public EntityManager(IShardEntityRegistry definitions)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _livestock = new LivingEntityStore(null, ".", AllocateEntityId);
        _experienceOrbs = new ExperienceOrbStore(null, ".", AllocateEntityId);
        _groundItems = new GroundItemStore(null, ".", AllocateEntityId);
        _fishing = new FishingWorld(AllocateEntityId);
        _projectiles = new ProjectileWorld(AllocateEntityId);
    }

    /// <summary>Bind persistence once to the actual Gameplay world directory.
    /// Entities owns the store; Gameplay supplies the already-established save
    /// path so AMLIVE01 worlds remain readable after the Shard migration.</summary>
    public void BindWorldEntities(string? directory, string worldPath,
        IShardItemRegistry? items = null)
    {
        lock (_gate)
        {
            if (_worldBound) return;
            _livestock = new LivingEntityStore(directory, worldPath, AllocateEntityId);
            _experienceOrbs = new ExperienceOrbStore(directory, worldPath, AllocateEntityId);
            _groundItems = new GroundItemStore(directory, worldPath, AllocateEntityId, items);
            _worldBound = true;
        }
    }

    LivingEntity[] IShardLivingEntities.Snapshot() => _livestock.Snapshot();
    public bool TryGet(int entityId, out LivingEntity entity) =>
        _livestock.TryGet(entityId, out entity);
    public bool TrySpawnSheep(double x, double y, double z,
        out LivingEntity created, int color = 0) =>
        _livestock.TrySpawnSheep(x, y, z, out created, color);
    bool IShardLivingEntities.TryDamage(int id, float points,
        out LivingEntity updated, out bool killed) =>
        _livestock.TryDamage(id, points, out updated, out killed);
    bool IShardLivingEntities.TryShear(int id, out LivingEntity changed) =>
        _livestock.TryShear(id, out changed);
    bool IShardLivingEntities.TryTeleport(int id, double x, double y, double z) =>
        _livestock.TryTeleport(id, x, y, z);
    public LivingEntity[] Regrow(long nowMs, int requiredMs = 180_000) =>
        _livestock.Regrow(nowMs, requiredMs);
    public void Save() => _livestock.Save();

    ExperienceOrb[] IShardExperienceOrbs.Snapshot() =>
        _experienceOrbs.Snapshot();
    bool IShardExperienceOrbs.TrySpawn(int value, double x, double y,
        double z, out ExperienceOrb orb, long? nowMs) =>
        _experienceOrbs.TrySpawn(value, x, y, z, out orb, nowMs);
    void IShardExperienceOrbs.Simulate(IShardBlockWorld world, long? nowMs) =>
        _experienceOrbs.Simulate(world, nowMs);
    int IShardExperienceOrbs.TryCollect(int id, long? nowMs) =>
        _experienceOrbs.TryCollect(id, nowMs);
    void IShardExperienceOrbs.Save() => _experienceOrbs.Save();

    GroundItem[] IShardGroundItems.Snapshot() => _groundItems.Snapshot();
    bool IShardGroundItems.TrySpawn(int itemId, int count, double x, double y,
        double z, out GroundItem entity, long? nowMilliseconds) =>
        _groundItems.TrySpawn(itemId, count, x, y, z, out entity, nowMilliseconds);
    bool IShardGroundItems.TrySpawn(ItemStack stack, double x, double y,
        double z, out GroundItem entity, long? nowMilliseconds) =>
        _groundItems.TrySpawn(stack, x, y, z, out entity, nowMilliseconds);
    bool IShardGroundItems.TryRemove(int entityId) => _groundItems.TryRemove(entityId);
    bool IShardGroundItems.TryDiscard(int entityId) => _groundItems.TryDiscard(entityId);
    void IShardGroundItems.Simulate(IShardBlockWorld world, long? nowMs) =>
        _groundItems.Simulate(world, nowMs);
    int IShardGroundItems.TryCollect(int entityId, IShardPickupInventory inventory,
        out GroundItem remaining, long? nowMs) =>
        _groundItems.TryCollect(entityId, inventory, out remaining, nowMs);
    void IShardGroundItems.Save() => _groundItems.Save();

    ProjectileEntity[] IShardProjectileWorld.Snapshot() => _projectiles.Snapshot();
    bool IShardProjectileWorld.TryLaunch(Guid owner, ProjectileKind kind,
        PlayerPose source, double speed, float damage,
        ItemStack recoverable, out ProjectileEntity projectile,
        ItemStack payload) =>
        _projectiles.TryLaunch(owner, kind, source, speed, damage, recoverable,
            out projectile, payload);
    ProjectileFrame IShardProjectileWorld.Tick(IShardBlockWorld world,
        IReadOnlyList<ProjectilePlayer> players,
        IReadOnlyList<ShardEntityInstance> mobs) =>
        _projectiles.Tick(world, players, mobs);

    FishingBobber[] IShardFishingWorld.Snapshot() => _fishing.Snapshot();
    bool IShardFishingWorld.HasHook(Guid owner) => _fishing.HasHook(owner);
    bool IShardFishingWorld.TryCast(Guid owner, int ownerEntityId,
        PlayerPose pos, out FishingBobber bobber, int waitReductionSeconds) =>
        _fishing.TryCast(owner, ownerEntityId, pos, out bobber,
            waitReductionSeconds);
    bool IShardFishingWorld.TryReel(Guid owner, out FishingReel result,
        long? nowMilliseconds) => _fishing.TryReel(owner, out result,
            nowMilliseconds);
    bool IShardFishingWorld.Cancel(Guid owner) => _fishing.Cancel(owner);
    FishingBobber[] IShardFishingWorld.Tick(IShardBlockWorld world,
        long? nowMilliseconds, Random? random) =>
        _fishing.Tick(world, nowMilliseconds, random);
    bool IShardFishingWorld.CheckOpenWater(IShardBlockWorld world,
        FishingBobber bobber) => _fishing.CheckOpenWater(world, bobber);
    (string Item, int Xp) IShardFishingWorld.RollStarterLoot(Random? random) =>
        _fishing.RollStarterLoot(random);

    private ShardEntityInstance FromLivestock(LivingEntity sheep)
    {
        int protocolId = _definitions.TryGetByIdentifier("minecraft:sheep",
            out var definition) ? definition.ProtocolId : 114;
        return new ShardEntityInstance(sheep.EntityId, sheep.Uuid, sheep.Kind,
            protocolId, sheep.X, sheep.Y, sheep.Z, 0, 0, sheep.Health)
        {
            Sheared = sheep.Sheared,
            Color = sheep.Color
        };
    }

    public int AllocateEntityId()
    {
        int next = Interlocked.Increment(ref _nextEntityId);
        if (next <= 0) throw new InvalidOperationException("Entity ID space exhausted.");
        return next;
    }

    public ShardEntityInstance[] Snapshot()
    {
        lock (_gate)
            return _instances.Values.Concat(_livestock.Snapshot().Select(FromLivestock))
                .OrderBy(e => e.EntityId).ToArray();
    }

    public bool TrySpawn(string definitionId, double x, double y, double z,
        out ShardEntityInstance entity)
    {
        entity = default;
        if (!ValidPosition(x, y, z) ||
            !_definitions.TryGetByIdentifier(definitionId, out var definition) ||
            !definition.Spawnable)
            return false;
        lock (_gate)
        {
            if (definition.Identifier == "minecraft:sheep")
            {
                if (!_livestock.TrySpawnSheep(x, y, z, out LivingEntity sheep))
                    return false;
                entity = FromLivestock(sheep);
                return true;
            }
            if (_instances.Count + _livestock.Snapshot().Length >= MaximumEntities)
                return false;
            entity = new ShardEntityInstance(AllocateEntityId(), Guid.NewGuid(),
                definition.Identifier, definition.ProtocolId, x, y, z, 0, 0,
                definition.MaxHealth);
            _instances.Add(entity.EntityId, entity);
            return true;
        }
    }

    public bool TryRemove(int entityId)
    {
        lock (_gate)
        {
            _sheared.Remove(entityId);
            return _instances.Remove(entityId) || _livestock.TryRemove(entityId);
        }
    }

    public bool TryTeleport(int entityId, double x, double y, double z,
        float yaw, float pitch)
    {
        if (!ValidPosition(x, y, z) || !float.IsFinite(yaw) || !float.IsFinite(pitch))
            return false;
        lock (_gate)
        {
            if (_livestock.TryGet(entityId, out _))
                return _livestock.TryTeleport(entityId, x, y, z);
            if (!_instances.TryGetValue(entityId, out var entity)) return false;
            _instances[entityId] = entity with { X = x, Y = y, Z = z, Yaw = yaw, Pitch = pitch };
            return true;
        }
    }

    public bool TryDamage(int entityId, double amount, out ShardEntityInstance updated)
    {
        updated = default;
        if (!double.IsFinite(amount) || amount <= 0) return false;
        lock (_gate)
        {
            if (_livestock.TryGet(entityId, out _))
            {
                if (!_livestock.TryDamage(entityId, (float)Math.Min(amount, float.MaxValue),
                        out LivingEntity livestock, out bool killed)) return false;
                updated = FromLivestock(livestock);
                if (killed) EnqueueDeath(updated);
                return true;
            }
            if (!_instances.TryGetValue(entityId, out var entity) ||
                !_definitions.TryGetByIdentifier(entity.DefinitionId, out var definition) ||
                definition.Invulnerable || entity.Health is not double health)
                return false;
            updated = entity with { Health = Math.Max(0, health - amount) };
            if (updated.Health <= 0)
            {
                _instances.Remove(entityId);
                _sheared.Remove(entityId);
                EnqueueDeath(updated);
            }
            else _instances[entityId] = updated;
            return true;
        }
    }

    public bool TryKill(int entityId, out ShardEntityInstance killed)
    {
        killed = default;
        lock (_gate)
        {
            if (_livestock.TryGet(entityId, out _))
            {
                if (!_livestock.TryDamage(entityId, float.MaxValue,
                        out LivingEntity livestock, out bool defeated) || !defeated)
                    return false;
                killed = FromLivestock(livestock);
                EnqueueDeath(killed);
                return true;
            }
            if (!_instances.TryGetValue(entityId, out var entity) ||
                !_definitions.TryGetByIdentifier(entity.DefinitionId, out var definition) ||
                definition.Invulnerable)
                return false;
            killed = entity with { Health = entity.Health.HasValue ? 0 : null };
            _instances.Remove(entityId);
            _sheared.Remove(entityId);
            EnqueueDeath(killed);
            return true;
        }
    }

    public bool TryMilk(int entityId, out ShardEntityInstance entity)
    {
        entity = default;
        lock (_gate)
        {
            if (!_instances.TryGetValue(entityId, out var found) ||
                !_definitions.TryGetById(found.ProtocolId, out var kind) ||
                kind.Identifier is not ("minecraft:cow" or "minecraft:mooshroom" or
                    "minecraft:goat"))
                return false;
            entity = found;
            return true;
        }
    }

    public bool TryShear(int entityId, out ShardEntityInstance entity)
    {
        entity = default;
        lock (_gate)
        {
            if (_livestock.TryGet(entityId, out _))
            {
                if (!_livestock.TryShear(entityId, out LivingEntity sheep)) return false;
                entity = FromLivestock(sheep);
                return true;
            }
            if (!_instances.TryGetValue(entityId, out var found) ||
                !_definitions.TryGetById(found.ProtocolId, out var kind) ||
                kind.Identifier != "minecraft:sheep" || _sheared.Contains(entityId))
                return false;
            _sheared.Add(entityId);
            entity = found;
            return true;
        }
    }

    public bool IsSheared(int entityId)
    {
        lock (_gate)
            return _sheared.Contains(entityId) ||
                _livestock.TryGet(entityId, out LivingEntity sheep) && sheep.Sheared;
    }

    public bool TryDamageAndTakeDeath(int entityId, double amount,
        out ShardEntityInstance updated, out bool killed)
    {
        lock (_gate)
        {
            // _gate is a re-entrant Monitor, so this transaction cannot
            // interleave with a tick draining death events.
            if (!TryDamage(entityId, amount, out updated))
            {
                killed = false;
                return false;
            }
            killed = updated.Health is <= 0 && TryTakeDeath(entityId, out _);
            return true;
        }
    }

    public bool TryTakeDeath(int entityId, out ShardEntityInstance killed)
    {
        killed = default;
        if (entityId <= 0) return false;
        lock (_gate)
        {
            if (!_deaths.Any(death => death.EntityId == entityId))
                return false;
            int count = _deaths.Count;
            for (int i = 0; i < count; i++)
            {
                ShardEntityInstance death = _deaths.Dequeue();
                if (death.EntityId == entityId)
                {
                    killed = death;
                    continue;
                }
                _deaths.Enqueue(death);
            }
            return true;
        }
    }

    public ShardEntityInstance[] DrainDeaths()
    {
        lock (_gate)
        {
            var deaths = _deaths.ToArray();
            _deaths.Clear();
            return deaths;
        }
    }

    private void EnqueueDeath(ShardEntityInstance dead)
    {
        // Bound notification memory if gameplay is disabled while another
        // Shard repeatedly creates and kills entities.
        if (_deaths.Count >= MaximumEntities) _deaths.Dequeue();
        _deaths.Enqueue(dead);
    }

    private static bool ValidPosition(double x, double y, double z) =>
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z) &&
        Math.Abs(x) <= MaximumCoordinate && Math.Abs(z) <= MaximumCoordinate &&
        y is >= -2048 and <= 4096;
}
