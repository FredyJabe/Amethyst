using Amethyst.Shards;

namespace Amethyst;

/// <summary>Shared deterministic combat arithmetic. The server validates
/// entity, reach, cooldown and game mode before calling these helpers.</summary>
public static class CombatRules
{
    public const double MeleeReach = 3.0;
    public const long MinimumAttackIntervalMs = 120;

    public static float MeleeDamage(string heldItem)
    {
        string name = heldItem.StartsWith("minecraft:", StringComparison.Ordinal)
            ? heldItem["minecraft:".Length..] : heldItem;
        if (name.EndsWith("_sword", StringComparison.Ordinal))
            return MaterialDamage(name) + 3f;
        if (name.EndsWith("_axe", StringComparison.Ordinal))
            return MaterialDamage(name) + 4f;
        if (name.EndsWith("_spear", StringComparison.Ordinal)) return 5f;
        if (name == "mace") return 6f;
        if (name == "trident") return 9f;
        return 1f;
    }

    /// <summary>Strength and Weakness modify the attack-damage attribute
    /// before the onAttack skill pipeline and the victim's armor mitigation.
    /// Effect amplifiers are zero-based.</summary>
    public static float MeleeDamageWithEffects(string heldItem,
        IEnumerable<PlayerActiveEffects.Active> effects)
    {
        float value = MeleeDamage(heldItem);
        foreach (var effect in effects)
        {
            switch (effect.Id)
            {
                case "minecraft:strength":
                    value += 3f * (effect.Amplifier + 1);
                    break;
                case "minecraft:weakness":
                    value -= 4f * (effect.Amplifier + 1);
                    break;
            }
        }
        return Math.Max(0, value);
    }

    private static float MaterialDamage(string n) =>
        n.StartsWith("wooden_", StringComparison.Ordinal) ||
        n.StartsWith("golden_", StringComparison.Ordinal) ? 1 :
        n.StartsWith("stone_", StringComparison.Ordinal) ? 2 :
        n.StartsWith("copper_", StringComparison.Ordinal) ? 2 :
        n.StartsWith("iron_", StringComparison.Ordinal) ? 3 :
        n.StartsWith("diamond_", StringComparison.Ordinal) ? 4 :
        n.StartsWith("netherite_", StringComparison.Ordinal) ? 5 : 1;

    public static float ArmorPoints(ItemStack[] slots, IShardItemRegistry? items)
    {
        if (items is null || slots.Length < 9) return 0;
        float total = 0;
        for (int slot = 5; slot <= 8; slot++)
        {
            if (slots[slot].Count == 0 ||
                !items.TryGetById(slots[slot].ItemId, out var item)) continue;
            string name = item.Identifier;
            int body = slot switch { 5 => 2, 6 => 3, 7 => 2, _ => 1 };
            int material = name.StartsWith("minecraft:leather_", StringComparison.Ordinal) ? 1 :
                name.StartsWith("minecraft:golden_", StringComparison.Ordinal) ? 2 :
                name.StartsWith("minecraft:chainmail_", StringComparison.Ordinal) ? 2 :
                name.StartsWith("minecraft:iron_", StringComparison.Ordinal) ? 3 :
                name.StartsWith("minecraft:copper_", StringComparison.Ordinal) ? 2 :
                name.StartsWith("minecraft:diamond_", StringComparison.Ordinal) ? 4 :
                name.StartsWith("minecraft:netherite_", StringComparison.Ordinal) ? 4 : 0;
            if (item.Identifier == "minecraft:turtle_helmet") total += 2;
            else if (material > 0) total += Math.Max(1, body + material - 2);
        }
        return Math.Min(20, total);
    }

    public static float Mitigate(float damage, float armor, float toughness = 0)
    {
        if (damage <= 0) return 0;
        double effective = Math.Max(armor / 5d,
            armor - damage / (2 + toughness / 4d));
        return damage * (1 - (float)Math.Clamp(effective, 0, 20) / 25f);
    }

    public static bool InReach(PlayerPose source, PlayerPose target,
        double reach = MeleeReach)
    {
        double dx = source.X - target.X, dy = (source.Y + 1.3) - (target.Y + 0.9),
            dz = source.Z - target.Z;
        return dx * dx + dy * dy + dz * dz <= reach * reach;
    }

    public static (double X, double Z) Knockback(PlayerPose attacker, PlayerPose victim,
        double strength = 0.4)
    {
        double dx = victim.X - attacker.X, dz = victim.Z - attacker.Z;
        double magnitude = Math.Sqrt(dx * dx + dz * dz);
        if (magnitude < 0.00001) return (0, strength);
        return (strength * dx / magnitude, strength * dz / magnitude);
    }
}
