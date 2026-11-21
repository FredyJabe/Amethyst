namespace Amethyst.Shards;

/// <summary>Item-Shard-owned YAML loot skill packs for world/entity deaths.
/// Even a pack containing zero Skills is meaningful: it replaces the
/// legacy default drops rather than silently falling back to hardcoded loot.</summary>
public interface IShardLootRegistry
{
    int Count { get; }
    bool HasDefinition(string ownerId);
    /// <summary>True when the owner has a user-supplied YAML override.
    /// Overrides take precedence over the pinned vanilla data.</summary>
    bool HasOverride(string ownerId);
    IReadOnlyList<ShardSkillInstruction> Instructions { get; }
}
