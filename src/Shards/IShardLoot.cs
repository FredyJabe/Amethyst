namespace Amethyst.Shards;

/// <summary>One configured independent roll from an Items Shard loot table.
/// Chance uses the inclusive range [0,1]; the result count is rolled uniformly
/// between Min and Max (inclusive). ItemId is the vanilla registry wire ID.</summary>
public readonly record struct ShardLootEntry(
    int ItemId, int Min, int Max, double Chance);

public readonly record struct ShardLootStack(int ItemId, int Count);

/// <summary>Server-defined item drops associated with an entity identifier.
/// Namespaced identifiers and Mythic-style short names are supported. A table
/// with an empty Drops list explicitly produces no loot.</summary>
public sealed record ShardLootTable(string EntityId, IReadOnlyList<ShardLootEntry> Drops);

/// <summary>Read-only validated loot tables supplied by the Items Shard.
/// Entities report deaths; Gameplay performs physical item spawning.</summary>
public interface IShardLootRegistry
{
    int TableCount { get; }
    IReadOnlyList<ShardLootTable> Definitions { get; }
    /// <returns>False when no table is defined. True with an empty result
    /// means a table exists but no items were rolled.</returns>
    bool TryRollEntity(string entityId, out ShardLootStack[] drops, Random? random = null);
}
