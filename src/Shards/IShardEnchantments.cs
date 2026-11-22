namespace Amethyst.Shards;

/// <summary>Enchantment definition from the Java 26.3 registry, with
/// validated YAML-authored Skills for the core SkillEngine.</summary>
public sealed record ShardEnchantmentDefinition(
    string Identifier, int MaxLevel, int Weight, int AnvilCost,
    IReadOnlyList<string> SupportedItems, IReadOnlyList<string> Conflicts,
    bool Treasure, bool Curse,
    IReadOnlyList<ShardSkillInstruction> Skills,
    IReadOnlyList<ShardStatModifier> Stats);

public interface IShardEnchantmentRegistry
{
    int Count { get; }
    IReadOnlyList<ShardEnchantmentDefinition> All { get; }
    bool TryGet(string identifier, out ShardEnchantmentDefinition definition);
    bool CanApply(string identifier, ShardItemDefinition item);
    bool ConflictsWith(string first, string second);
}
