namespace Amethyst.Shards;

/// <summary>Parsed, validated Mythic-style skill. The engine knows no
/// item IDs, entities, combat mechanics or other Shard-specific behavior.</summary>
public sealed record ShardSkillInstruction(
    string OwnerId, string Mechanic, string Trigger, string? Target,
    IReadOnlyDictionary<string, string> Arguments);

/// <summary>Event data supplied by a Shard when dispatching a trigger.
/// OwnerId is the exact entity/item/other object definition key;
/// FallbackOwnerId is consulted only when OwnerId has no skill list.</summary>
public sealed record ShardSkillEvent(
    string OwnerId, string Trigger, double X = 0, double Y = 0, double Z = 0,
    int EntityId = 0, string? FallbackOwnerId = null, Random? Random = null)
{
    public Random Rng => Random ?? System.Random.Shared;
}

public delegate void ShardSkillValidator(ShardSkillInstruction skill);
public delegate ValueTask ShardSkillExecutor(
    ShardSkillInstruction skill, ShardSkillEvent context, CancellationToken ct);

/// <summary>Core-owned registry, parser and event dispatcher. All enabled
/// Shards share one instance. Shards contribute mechanics and YAML skill packs;
/// the ShardHost cleans up contributions when a Shard is disabled.</summary>
public interface IShardSkillEngine
{
    int OwnerCount { get; }
    IReadOnlyList<ShardSkillInstruction> Definitions { get; }
    bool TryGetSkills(string ownerId, out IReadOnlyList<ShardSkillInstruction> skills);
    Task<bool> DispatchAsync(ShardSkillEvent context, CancellationToken ct = default);
}

/// <summary>Side-effect capability supplied by Gameplay. Items' dropItem
/// mechanic resolves it when invoked rather than owning entity physics.</summary>
public interface IShardItemDropSink
{
    bool TryDropItem(int itemId, int count, double x, double y, double z);
}
