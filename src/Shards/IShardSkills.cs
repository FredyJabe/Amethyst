namespace Amethyst.Shards;

/// <summary>Supported entity event triggers in Items Shard skills.</summary>
public enum ShardSkillTrigger { OnDeath }

/// <summary>Skills are data-driven and can gain new mechanics independently
/// of Entities or the network kernel.</summary>
public enum ShardSkillAction { DropItem }

/// <summary>Validated server-side skill definition for an entity ID.</summary>
public readonly record struct ShardSkillDefinition(
    string OwnerId, ShardSkillAction Action, ShardSkillTrigger Trigger,
    int ItemId, int Min, int Max, double Chance);

/// <summary>An executable dropItem result; physical spawning belongs to Gameplay.</summary>
public readonly record struct ShardSkillDrop(int ItemId, int Count);

public interface IShardSkillRegistry
{
    int OwnerCount { get; }
    IReadOnlyList<ShardSkillDefinition> Definitions { get; }
    /// <summary>Returns true even for an explicitly empty Skills list.</summary>
    bool TryGetSkills(string entityId, out IReadOnlyList<ShardSkillDefinition> skills);
    /// <summary>Run matching skills; false means the entity has no definition.</summary>
    bool TryExecute(string entityId, ShardSkillTrigger trigger,
        out ShardSkillDrop[] drops, Random? random = null);
}
