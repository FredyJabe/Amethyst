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
    /// <summary>XP awarded to an eligible player killer. Configure a fixed
    /// number or inclusive [min, max] in the Entities Shard's YAML.</summary>
    public int ExperienceMin { get; init; }
    public int ExperienceMax { get; init; }
}

public readonly record struct ShardEntityInstance(
    int EntityId, Guid Uuid, string DefinitionId, int ProtocolId,
    double X, double Y, double Z, float Yaw, float Pitch, double? Health)
{
    /// <summary>Server-owned livestock metadata captured before removal,
    /// so onDeath skills can evaluate the actual killed animal's state.</summary>
    public bool Sheared { get; init; }
    public int Color { get; init; }
}

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
/// <summary>Server-authoritative interactions with living entities. The
/// protocol and inventory belong to Gameplay; Entities owns species rules
/// and per-entity sheared state. Successful actions can emit Core triggers.</summary>
public interface IShardEntityInteractions
{
    /// <summary>Check and process milking. No entity state is consumed;
    /// cows, mooshrooms and goats can be milked repeatedly.</summary>
    bool TryMilk(int entityId, out ShardEntityInstance entity);
    /// <summary>Mark a sheep sheared once; repeat calls cannot duplicate wool.
    /// Vanilla wool colour, regrowth and equipment metadata remain future work.</summary>
    bool TryShear(int entityId, out ShardEntityInstance entity);
    bool IsSheared(int entityId);
}

/// <summary>Authoritative entity store and network identifier allocator.</summary>
public interface IShardEntityManager
{
    /// <summary>Bind legacy-compatible world persistence once, before
    /// Gameplay begins spawning or viewing living entities.</summary>
    void BindWorldEntities(string? directory, string worldPath,
        IShardItemRegistry? items = null);
    int AllocateEntityId();
    ShardEntityInstance[] Snapshot();
    bool TrySpawn(string definitionId, double x, double y, double z,
        out ShardEntityInstance entity);
    bool TryRemove(int entityId);
    bool TryTeleport(int entityId, double x, double y, double z,
        float yaw, float pitch);
    bool TryDamage(int entityId, double amount, out ShardEntityInstance updated);
    /// <summary>Atomic player-attack damage and one-shot death collection.
    /// Killed entities are removed from the general death queue before other
    /// tick consumers can process them without attacker attribution.</summary>
    bool TryDamageAndTakeDeath(int entityId, double amount,
        out ShardEntityInstance updated, out bool killed);
    /// <summary>Kill one managed entity, honoring its invulnerability flag.
    /// Unlike TryRemove, a kill creates a death event for loot processing.</summary>
    bool TryKill(int entityId, out ShardEntityInstance killed);
    /// <summary>Consume deaths once; Gameplay owns the drop generation.
    /// Despawns (TryRemove) are never counted as deaths.</summary>
    /// <summary>Consume a queued death from a specific instance.</summary>
    bool TryTakeDeath(int entityId, out ShardEntityInstance killed);
    ShardEntityInstance[] DrainDeaths();
}
