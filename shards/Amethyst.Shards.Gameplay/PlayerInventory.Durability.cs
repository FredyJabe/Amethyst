using Amethyst.Shards;

namespace Amethyst;

public sealed partial class PlayerInventory
{
    private static bool IsDefinitionUnbreakable(ShardItemDefinition definition) =>
        definition.Components is not null &&
        definition.Components.TryGetValue("minecraft:unbreakable", out string? value) &&
        value == "true";

    /// <summary>Wear a specific equipped slot after a successful gameplay
    /// action. Slot 5-8 is armor; 36-44 selected hotbar; 45 is offhand.</summary>
    public bool DamageSlot(int slot, int expectedItemId, int points,
        Random? random = null)
    {
        if (!ValidSlot(slot) || points <= 0) return false;
        lock (_sync)
        {
            ItemStack old = _slots[slot];
            if (old.Count != 1 || old.ItemId != expectedItemId ||
                _items is null || !_items.TryGetById(old.ItemId, out var item) ||
                item.MaxDurability <= 0 ||
                IsDefinitionUnbreakable(item)) return false;
            ItemStack next = DurabilityRules.ApplyWear(old, item.MaxDurability,
                points, slot is >= 5 and <= 8 &&
                DurabilityRules.IsArmor(item.Identifier), random,
                PreventsDurabilityWear);
            if (next == old) return false;
            _slots[slot] = next;
            try { Persist(); _revision++; }
            catch { _slots[slot] = old; throw; }
            return true;
        }
    }

    /// <summary>Apply armor durability after a real, positive combat or
    /// environmental damage event. The host must prevent Creative wear.</summary>
    public int DamageArmor(int points, Random? random = null)
    {
        if (points <= 0 || _items is null) return 0;
        lock (_sync)
        {
            ItemStack[] previous = (ItemStack[])_slots.Clone();
            int changed = 0;
            for (int slot = 5; slot <= 8; slot++)
            {
                ItemStack old = _slots[slot];
                if (old.Count != 1 ||
                    !_items.TryGetById(old.ItemId, out var item) ||
                    item.MaxDurability <= 0 ||
                    IsDefinitionUnbreakable(item)) continue;
                ItemStack next = DurabilityRules.ApplyWear(old, item.MaxDurability,
                    points, armor: true, random: random,
                    preventsWear: PreventsDurabilityWear);
                if (next == old) continue;
                _slots[slot] = next;
                changed++;
            }
            if (changed == 0) return 0;
            try { Persist(); _revision++; }
            catch { Array.Copy(previous, _slots, _slots.Length); throw; }
            return changed;
        }
    }

    /// <summary>Consume XP orbs for Mending, returning the remaining XP for
    /// the experience level system. Vanilla only selects worn equipped items.</summary>
    public (int ExperienceLeft, int RepairedSlot) RepairFromExperience(
        int experience, Random? random = null)
    {
        if (experience <= 0) return (experience, -1);
        lock (_sync)
        {
            ItemStack[] snapshot = (ItemStack[])_slots.Clone();
            var (left, changed, slot) = DurabilityRules.ApplyMending(
                _slots, 36 + _selected, experience, _items, random);
            if (!changed) return (experience, -1);
            try { Persist(); _revision++; }
            catch { Array.Copy(snapshot, _slots, _slots.Length); throw; }
            return (left, slot);
        }
    }

    /// <summary>Insert a complete item stack (e.g. returned crafting inputs
    /// or a ground entity), keeping damage and enchantment components intact.
    /// Never merge stacks with different component patches.</summary>
    public int GiveStack(ItemStack incoming)
    {
        if (incoming.Count <= 0 || incoming.ItemId <= 0) return 0;
        int maximum = MaxStack(incoming.ItemId);
        if (maximum == 0) return 0;
        bool extra = incoming.Damage > 0 || incoming.Unbreaking > 0 ||
            incoming.Mending || incoming.Unbreakable ||
            incoming.RepairCost > 0 || !string.IsNullOrEmpty(incoming.EnchantmentData);
        if ((extra && incoming.Count != 1) || !ValidStack(incoming)) return 0;
        lock (_sync)
        {
            ItemStack[] previous = (ItemStack[])_slots.Clone();
            int remaining = incoming.Count;
            foreach (int i in Enumerable.Range(36, 9)
                .Concat(Enumerable.Range(9, 27)))
            {
                ItemStack slot = _slots[i];
                if (slot.Count == 0 || !SameItemData(slot, incoming) ||
                    slot.Count >= maximum) continue;
                int amount = Math.Min(maximum - slot.Count, remaining);
                _slots[i] = slot with { Count = slot.Count + amount };
                remaining -= amount;
                if (remaining == 0) break;
            }
            foreach (int i in Enumerable.Range(36, 9)
                .Concat(Enumerable.Range(9, 27)))
            {
                if (remaining == 0) break;
                if (_slots[i].Count != 0) continue;
                int amount = Math.Min(maximum, remaining);
                _slots[i] = incoming with { Count = amount };
                remaining -= amount;
            }
            if (remaining == incoming.Count) return 0;
            try { Persist(); _revision++; }
            catch { Array.Copy(previous, _slots, _slots.Length); throw; }
            return incoming.Count - remaining;
        }
    }

    /// <summary>Consume the exact authoritative held stack after a validated
    /// ground drop has been created. Revision and persistence are transaction-
    /// protected, and a mismatch never removes a different item.</summary>
    public bool TryDropHeld(ItemStack expected, int count)
    {
        if (expected.Count <= 0 || count is < 1 || count > expected.Count)
            return false;
        lock (_sync)
        {
            int slot = SelectedSlot;
            ItemStack current = _slots[slot];
            if (current != expected) return false;
            _slots[slot] = current.Count == count ? ItemStack.Empty :
                current with { Count = current.Count - count };
            try { Persist(); _revision++; }
            catch { _slots[slot] = current; throw; }
            return true;
        }
    }

    /// <summary>Consume ammunition from the hotbar/main inventory. The
    /// transaction either consumes the full amount or leaves all slots
    /// untouched. Supports arrows for bows and crossbows.</summary>
    public bool TryConsumeItem(int itemId, int count)
    {
        if (itemId <= 0 || count <= 0) return false;
        lock (_sync)
        {
            int available = _slots.Skip(9).Take(36).Where(s => s.ItemId == itemId)
                .Sum(s => s.Count);
            if (available < count) return false;
            ItemStack[] previous = (ItemStack[])_slots.Clone();
            int left = count;
            foreach (int index in Enumerable.Range(36, 9)
                .Concat(Enumerable.Range(9, 27)))
            {
                ItemStack stack = _slots[index];
                if (stack.ItemId != itemId || left == 0) continue;
                int taken = Math.Min(left, stack.Count);
                _slots[index] = stack.Count == taken ? ItemStack.Empty :
                    stack with { Count = stack.Count - taken };
                left -= taken;
            }
            try { Persist(); _revision++; }
            catch { Array.Copy(previous, _slots, _slots.Length); throw; }
            return true;
        }
    }

    public bool HasItem(int itemId, int count = 1)
    {
        lock (_sync) return _slots.Where((s, i) => i >= 9)
            .Sum(s => s.ItemId == itemId ? s.Count : 0) >= count;
    }

    /// <summary>Consume an expected stack from a particular hand/offhand.
    /// Used when throwing a trident, pearl, egg or snowball.</summary>
    public bool ConsumeSlot(int slot, int itemId, bool creative = false)
    {
        if (creative) return true;
        if (slot is < 36 or > 45) return false;
        lock (_sync)
        {
            ItemStack original = _slots[slot];
            if (original.ItemId != itemId || original.Count < 1) return false;
            _slots[slot] = original.Count == 1 ? ItemStack.Empty :
                original with { Count = original.Count - 1 };
            try { Persist(); _revision++; }
            catch { _slots[slot] = original; throw; }
            return true;
        }
    }

    /// <summary>Privileged full repair for server administration/tests;
    /// survival repair is performed through item combining/Mending/anvils.</summary>
    public bool RepairHeldCompletely()
    {
        lock (_sync)
        {
            int slot = 36 + _selected;
            ItemStack current = _slots[slot];
            if (current.Damage <= 0 || current.Count != 1) return false;
            _slots[slot] = current with { Damage = 0 };
            try { Persist(); _revision++; }
            catch { _slots[slot] = current; throw; }
            return true;
        }
    }

    /// <summary>Server-only enchantment writer. Validates the registered
    /// enchantment, item type and conflict set before mutating a held stack.
    /// Existing legacy fields keep their previous persistence semantics.</summary>
    public bool EnchantHeld(string enchantmentId, int level,
        IShardEnchantmentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!registry.TryGet(enchantmentId, out var enchantment) ||
            level is < 1 or > 255 || level > enchantment.MaxLevel)
            return false;
        lock (_sync)
        {
            int slot = 36 + _selected;
            ItemStack old = _slots[slot];
            if (old.Count != 1 || _items is null ||
                !_items.TryGetById(old.ItemId, out var item) ||
                !registry.CanApply(enchantment.Identifier, item))
                return false;
            foreach (var (id, existingLevel) in old.AllEnchantments())
                if (existingLevel > 0 && id != enchantment.Identifier &&
                    registry.ConflictsWith(id, enchantment.Identifier))
                    return false;
            ItemStack next;
            if (enchantment.Identifier == "minecraft:unbreaking")
                next = old with { Unbreaking = level };
            else if (enchantment.Identifier == "minecraft:mending")
                next = old with { Mending = true };
            else
            {
                var levels = old.AllEnchantments()
                    .Where(p => p.Id != enchantment.Identifier &&
                        p.Id is not ("minecraft:unbreaking" or "minecraft:mending"))
                    .ToDictionary(p => p.Id, p => p.Level, StringComparer.Ordinal);
                levels[enchantment.Identifier] = level;
                string encoded = string.Join(";", levels.OrderBy(p => p.Key,
                    StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
                next = old with { EnchantmentData = encoded };
            }
            if (next == old || !ValidStack(next)) return false;
            _slots[slot] = next;
            try { Persist(); _revision++; }
            catch { _slots[slot] = old; throw; }
            return true;
        }
    }

    /// <summary>Authoritative server-side modifier for enchanted/Unbreakable
    /// items. Client-provided creative slot components are never trusted.</summary>
    public bool ConfigureHeld(int unbreaking, bool mending, bool unbreakable)
    {
        if (unbreaking is < 0 or > 3) return false;
        lock (_sync)
        {
            int slot = 36 + _selected;
            ItemStack old = _slots[slot];
            if (old.Count != 1 || _items is null ||
                !_items.TryGetById(old.ItemId, out var definition) ||
                definition.MaxDurability <= 0) return false;
            ItemStack next = old with
            {
                Unbreaking = unbreaking,
                Mending = mending, Unbreakable = unbreakable
            };
            if (next == old) return false;
            _slots[slot] = next;
            try { Persist(); _revision++; }
            catch { _slots[slot] = old; throw; }
            return true;
        }
    }
}
