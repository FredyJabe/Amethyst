namespace Amethyst.Shards;

/// <summary>A Minecraft Java 26.3 entity type. ProtocolId, category, and geometry
/// belong to the vanilla client registry; an alias shares the base type's ID.
/// Gameplay fields are server definitions, not client-side registry additions.</summary>
public readonly record struct ShardEntityDefinition(
    int ProtocolId, string Identifier, string Category, double Width, double Height)
{
    /// <summary>Optional configured display label; not yet sent as entity metadata.</summary>
    public string? Display { get; init; }
    /// <summary>Optional server-managed health. Unspecified entities have no damage model yet.</summary>
    public double? MaxHealth { get; init; }
    public bool Spawnable { get; init; }
    public bool Invulnerable { get; init; }
}

public readonly record struct ShardEntityInstance(
    int EntityId, Guid Uuid, string DefinitionId, int ProtocolId,
    double X, double Y, double Z, float Yaw, float Pitch, double? Health);

/// <summary>Immutable version-pinned entity type catalog; custom YAML names
/// map to existing vanilla types, not to additional protocol IDs.</summary>
public interface IShardEntityRegistry
{
    string MinecraftVersion { get; }
    int Count { get; }
    IReadOnlyList<ShardEntityDefinition> All { get; }
    bool TryGetById(int protocolId, out ShardEntityDefinition definition);
    bool TryGetByIdentifier(string identifier, out ShardEntityDefinition definition);
}

/// <summary>Authoritative in-memory instances of Entities-Shard-owned spawns.
/// Players and dropped items must use AllocateEntityId to avoid network ID
/// collisions. World physics, AI, combat, drops and persistence are separate
/// systems and are not implied by this service.</summary>
public interface IShardEntityManager
{
    int AllocateEntityId();
    ShardEntityInstance[] Snapshot();
    bool TrySpawn(string definitionId, double x, double y, double z,
        out ShardEntityInstance entity);
    bool TryRemove(int entityId);
    bool TryTeleport(int entityId, double x, double y, double z,
        float yaw, float pitch);
    bool TryDamage(int entityId, double amount, out ShardEntityInstance updated);
    /// <summary>Kill one managed entity, honoring its invulnerability flag.
    /// Unlike TryRemove, a kill creates a death event for loot processing.</summary>
    bool TryKill(int entityId, out ShardEntityInstance killed);
    /// <summary>Consume deaths once; Gameplay owns the drop generation.
    /// Despawns (TryRemove) are never counted as deaths.</summary>
    ShardEntityInstance[] DrainDeaths();
}
