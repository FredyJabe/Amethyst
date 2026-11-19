using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>Thread-safe authoritative instance store. No automatic spawn,
/// AI or physics: other Shards can implement those via the typed service.</summary>
public sealed class EntityManager : IShardEntityManager
{
    private const int MaximumEntities = 10_000;
    private const double MaximumCoordinate = 30_000_000;
    private readonly IShardEntityRegistry _definitions;
    private readonly Dictionary<int, ShardEntityInstance> _instances = new();
    private readonly object _gate = new();
    private int _nextEntityId;

    public EntityManager(IShardEntityRegistry definitions) =>
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    public int AllocateEntityId()
    {
        int next = Interlocked.Increment(ref _nextEntityId);
        if (next <= 0) throw new InvalidOperationException("Entity ID space exhausted.");
        return next;
    }

    public ShardEntityInstance[] Snapshot()
    {
        lock (_gate) return _instances.Values.OrderBy(e => e.EntityId).ToArray();
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
            if (_instances.Count >= MaximumEntities) return false;
            entity = new ShardEntityInstance(AllocateEntityId(), Guid.NewGuid(),
                definition.Identifier, definition.ProtocolId, x, y, z, 0, 0,
                definition.MaxHealth);
            _instances.Add(entity.EntityId, entity);
            return true;
        }
    }

    public bool TryRemove(int entityId)
    {
        lock (_gate) return _instances.Remove(entityId);
    }

    public bool TryTeleport(int entityId, double x, double y, double z,
        float yaw, float pitch)
    {
        if (!ValidPosition(x, y, z) || !float.IsFinite(yaw) || !float.IsFinite(pitch))
            return false;
        lock (_gate)
        {
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
            if (!_instances.TryGetValue(entityId, out var entity) ||
                !_definitions.TryGetByIdentifier(entity.DefinitionId, out var definition) ||
                definition.Invulnerable || entity.Health is not double health)
                return false;
            updated = entity with { Health = Math.Max(0, health - amount) };
            if (updated.Health <= 0) _instances.Remove(entityId);
            else _instances[entityId] = updated;
            return true;
        }
    }

    private static bool ValidPosition(double x, double y, double z) =>
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z) &&
        Math.Abs(x) <= MaximumCoordinate && Math.Abs(z) <= MaximumCoordinate &&
        y is >= -2048 and <= 4096;
}
