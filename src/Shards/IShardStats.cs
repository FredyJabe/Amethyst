using System.Globalization;

namespace Amethyst.Shards;

/// <summary>
/// A server-side numeric stat modifier from an item's or enchantment's Stats YAML.
/// Stats do not register a skill mechanic: they are applied by authoritative
/// event publishers. Empty Trigger means the modifier applies on any event.
/// </summary>
public sealed record ShardStatModifier(
    string Stat, double Base, double PerLevel, string Operation, string? Trigger,
    IReadOnlyList<ShardSkillCondition> Conditions)
{
    public void Apply(ShardSkillEvent context, IShardSkillEngine? engine)
    {
        if (!string.IsNullOrEmpty(Trigger) &&
            !string.Equals(Trigger, context.Trigger, StringComparison.OrdinalIgnoreCase))
            return;
        if (Conditions.Count != 0 &&
            (engine is null || !engine.CheckConditions(Conditions, context)))
            return;
        double amount = Base + PerLevel * context.EnchantmentLevel;
        double previous = context.Runtime.Number(Stat);
        double result = Operation switch
        {
            "add" => previous + amount,
            "multiply" => previous * amount,
            "set" => amount,
            _ => throw new InvalidOperationException("Unsupported stat modifier operation.")
        };
        context.Runtime.SetNumber(Stat, result);
    }
}
