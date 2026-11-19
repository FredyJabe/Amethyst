using Amethyst.Shards;

namespace Amethyst;

/// <summary>Server-authoritative durability semantics shared by mining,
/// tool interactions, crafting repair, equipment damage, and XP collection.
/// No client-supplied item components are trusted by these methods.</summary>
public static class DurabilityRules
{
    /// <summary>For each point of attempted wear, Unbreaking prevents it with
    /// probability level/(level+1) for tools; for armor the chance is only
    /// 40% * level/(level+1), matching vanilla's armor penalty.</summary>
    public static ItemStack ApplyWear(ItemStack stack, int maximum, int points,
        bool armor = false, Random? random = null)
    {
        if (points <= 0 || stack.Count != 1 || maximum <= 0 ||
            stack.Unbreakable || stack.Damage >= maximum)
            return stack;
        random ??= Random.Shared;
        int taken = 0;
        for (int i = 0; i < points && taken < maximum; i++)
        {
            if (stack.Unbreaking > 0)
            {
                if (!armor || random.NextDouble() >= 0.6)
                {
                    if (random.Next(stack.Unbreaking + 1) > 0) continue;
                }
            }
            taken++;
        }
        if (taken == 0) return stack;
        int remaining = maximum - stack.Damage;
        return taken >= remaining ? ItemStack.Empty :
            stack with { Damage = stack.Damage + taken };
    }

    /// <summary>Vanilla crafting-grid/grindstone repair: sum remaining life
    /// plus 5% of maximum. Crafting removes non-curse enchantments and prior
    /// work metadata; this initial stack model supports Unbreaking/Mending.</summary>
    public static bool TryCombine(ItemStack first, ItemStack second,
        IShardItemRegistry? items, out ItemStack result)
    {
        result = ItemStack.Empty;
        if (items is null || first.ItemId != second.ItemId ||
            first.Count != 1 || second.Count != 1 ||
            !items.TryGetById(first.ItemId, out var definition) ||
            definition.MaxDurability <= 0)
            return false;
        int max = definition.MaxDurability;
        if (first.Damage < 0 || second.Damage < 0 ||
            first.Damage >= max || second.Damage >= max ||
            first.Unbreakable || second.Unbreakable)
            return false;
        int remainder = (max - first.Damage) + (max - second.Damage) +
            (int)Math.Floor(max * 0.05);
        result = new ItemStack(first.ItemId, 1)
        {
            Damage = Math.Max(0, max - remainder)
        };
        return true;
    }

    /// <summary>Anvil material repair math (25% of max durability per
    /// material). This computes durability and material consumption; the
    /// eventual anvil screen must separately enforce XP and repair penalties.</summary>
    public static bool TryMaterialRepair(ItemStack original, int materialCount,
        int maximum, out ItemStack result, out int consumed)
    {
        result = original;
        consumed = 0;
        if (original.Count != 1 || original.Damage <= 0 || maximum <= 0 ||
            materialCount <= 0 || original.Unbreakable) return false;
        int perMaterial = Math.Max(1, maximum / 4);
        int remaining = original.Damage;
        while (remaining > 0 && consumed < materialCount)
        {
            remaining = Math.Max(0, remaining - perMaterial);
            consumed++;
        }
        result = original with { Damage = remaining };
        return consumed > 0;
    }

    /// <summary>Mending converts one collected XP point into up to two
    /// durability points. Selects randomly among eligible worn stacks in
    /// the selected hand, offhand and armor slots. The caller owns XP
    /// accounting and can credit the remaining XP to the player.</summary>
    public static (int ExperienceLeft, bool Changed, int RepairedSlot) ApplyMending(
        ItemStack[] slots, int selectedSlot, int xp, IShardItemRegistry? items,
        Random? random = null)
    {
        if (xp <= 0 || items is null || slots.Length < 46 ||
            selectedSlot is < 36 or > 44) return (xp, false, -1);
        var candidates = new List<int>();
        foreach (int slot in new[] { selectedSlot, 45, 5, 6, 7, 8 })
        {
            ItemStack stack = slots[slot];
            if (stack.Count != 1 || stack.Damage <= 0 || !stack.Mending ||
                !items.TryGetById(stack.ItemId, out var definition) ||
                definition.MaxDurability <= 0)
                continue;
            candidates.Add(slot);
        }
        if (candidates.Count == 0) return (xp, false, -1);
        random ??= Random.Shared;
        int chosen = candidates[random.Next(candidates.Count)];
        ItemStack item = slots[chosen];
        int repair = Math.Min(item.Damage, (int)Math.Min((long)xp * 2, int.MaxValue));
        int cost = (repair + 1) / 2;
        slots[chosen] = item with { Damage = item.Damage - repair };
        return (xp - cost, true, chosen);
    }

    /// <summary>Returned as true only if the operation should be considered
    /// physical armor wear (a real positive damage event).</summary>
    public static bool IsArmor(string itemIdentifier) =>
        itemIdentifier.EndsWith("_helmet", StringComparison.Ordinal) ||
        itemIdentifier.EndsWith("_chestplate", StringComparison.Ordinal) ||
        itemIdentifier.EndsWith("_leggings", StringComparison.Ordinal) ||
        itemIdentifier.EndsWith("_boots", StringComparison.Ordinal) ||
        itemIdentifier == "minecraft:turtle_helmet";
}
