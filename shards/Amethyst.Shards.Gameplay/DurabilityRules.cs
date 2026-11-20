using Amethyst.Shards;

namespace Amethyst;

/// <summary>Server-authoritative durability semantics shared by mining,
/// tool interactions, crafting repair, equipment damage, and XP collection.
/// No client-supplied item components are trusted by these methods.</summary>
public static class DurabilityRules
{
    /// <summary>For every point of attempted wear, consult the configured
    /// ~onItemDamage skill decision. Durability arithmetic contains no
    /// hard-coded enchantment chance.</summary>
    public static ItemStack ApplyWear(ItemStack stack, int maximum, int points,
        bool armor = false, Random? random = null,
        Func<ItemStack, bool, Random, bool>? preventsWear = null)
    {
        if (points <= 0 || stack.Count != 1 || maximum <= 0 ||
            stack.Unbreakable || stack.Damage >= maximum)
            return stack;
        random ??= Random.Shared;
        int taken = 0;
        for (int i = 0; i < points && taken < maximum; i++)
        {
            if (preventsWear?.Invoke(stack, armor, random) == true) continue;
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

    /// <summary>Two-input grindstone preview: repair two matching damaged
    /// items or strip supported non-curse enchantments from one item.
    /// Returns true only for a meaningful transformation.</summary>
    public static bool TryGrindstone(ItemStack first, ItemStack second,
        IShardItemRegistry? items, out ItemStack result)
    {
        result = ItemStack.Empty;
        if (first.Count != 1) return false;
        if (second.Count > 0)
        {
            if (!TryCombine(first, second, items, out result)) return false;
            return true;
        }
        if (!first.Mending && first.Unbreaking == 0) return false;
        result = first with
        {
            Mending = false, Unbreaking = 0, RepairCost = 0
        };
        return true;
    }

    /// <summary>Preview an anvil repair/combination. Does not grant items or
    /// consume XP; the workstation commits the preview atomically.</summary>
    public static bool TryAnvil(ItemStack first, ItemStack second,
        IShardItemRegistry? items, out ItemStack result,
        out int materialConsumed, out int levelCost)
    {
        result = ItemStack.Empty;
        materialConsumed = 0;
        levelCost = 0;
        if (items is null || first.Count != 1 || second.Count <= 0 ||
            first.Unbreakable ||
            !items.TryGetById(first.ItemId, out var definition) ||
            definition.MaxDurability <= 0)
            return false;
        int max = definition.MaxDurability;
        if (first.ItemId == second.ItemId && second.Count == 1)
        {
            if (second.Unbreakable) return false;
            int remaining = (max - first.Damage) + (max - second.Damage) +
                (int)Math.Floor(max * 0.12);
            int damage = Math.Max(0, max - remaining);
            int unbreaking = Math.Max(first.Unbreaking, second.Unbreaking);
            if (first.Unbreaking > 0 &&
                first.Unbreaking == second.Unbreaking)
                unbreaking = Math.Min(3, unbreaking + 1);
            bool mending = first.Mending || second.Mending;
            if (damage == first.Damage &&
                unbreaking == first.Unbreaking && mending == first.Mending)
                return false;
            result = first with
            {
                Damage = damage, Unbreaking = unbreaking, Mending = mending,
                RepairCost = NextPriorWork(first, second)
            };
            materialConsumed = 1;
            levelCost = Math.Max(1, 2 + first.RepairCost + second.RepairCost +
                (unbreaking > first.Unbreaking ? 2 * unbreaking : 0) +
                (mending && !first.Mending ? 4 : 0));
            return true;
        }
        if (!items.TryGetById(second.ItemId, out var material) ||
            !CanRepairWith(definition.Identifier, material.Identifier) ||
            !TryMaterialRepair(first, second.Count, max,
                out ItemStack repaired, out materialConsumed))
            return false;
        result = repaired with
        {
            RepairCost = NextPriorWork(first, ItemStack.Empty)
        };
        levelCost = Math.Max(1, first.RepairCost + materialConsumed);
        return true;
    }

    private static int NextPriorWork(ItemStack first, ItemStack second) =>
        (int)Math.Min(1_000_000L,
            Math.Max(first.RepairCost, second.RepairCost) * 2L + 1);

    private static bool CanRepairWith(string tool, string material)
    {
        bool materialIsPlank = material.EndsWith("_planks", StringComparison.Ordinal);
        if (tool.StartsWith("minecraft:wooden_", StringComparison.Ordinal) ||
            tool == "minecraft:shield") return materialIsPlank;
        if (tool.StartsWith("minecraft:stone_", StringComparison.Ordinal))
            return material is "minecraft:cobblestone" or
                "minecraft:cobbled_deepslate" or "minecraft:blackstone";
        if (tool.StartsWith("minecraft:copper_", StringComparison.Ordinal))
            return material == "minecraft:copper_ingot";
        if (tool.StartsWith("minecraft:iron_", StringComparison.Ordinal) ||
            tool.StartsWith("minecraft:chainmail_", StringComparison.Ordinal) ||
            tool == "minecraft:shears")
            return material == "minecraft:iron_ingot";
        if (tool.StartsWith("minecraft:golden_", StringComparison.Ordinal))
            return material == "minecraft:gold_ingot";
        if (tool.StartsWith("minecraft:diamond_", StringComparison.Ordinal))
            return material == "minecraft:diamond";
        if (tool.StartsWith("minecraft:netherite_", StringComparison.Ordinal))
            return material == "minecraft:netherite_ingot";
        if (tool.StartsWith("minecraft:leather_", StringComparison.Ordinal))
            return material == "minecraft:leather";
        if (tool == "minecraft:elytra")
            return material == "minecraft:phantom_membrane";
        if (tool == "minecraft:turtle_helmet")
            return material is "minecraft:turtle_scute" or "minecraft:scute";
        if (tool is "minecraft:bow" or "minecraft:fishing_rod")
            return material == "minecraft:string";
        return false;
    }

    /// <summary>Each successful Survival anvil output has a 12% chance
    /// to advance from normal to chipped to damaged and finally break.
    /// Preserve the facing block state across damage stages.</summary>
    public static bool TryWearAnvil(ushort state, out ushort updated,
        Random? random = null)
    {
        updated = state;
        if (state is < 12962 or > 12973) return false;
        if ((random ?? Random.Shared).NextDouble() >= 0.12) return false;
        updated = state >= 12970 ? (ushort)0 : (ushort)(state + 4);
        return true;
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
