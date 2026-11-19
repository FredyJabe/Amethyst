using Amethyst.Shards;

namespace Amethyst;

public sealed partial class PlayerInventory
{
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
                item.MaxDurability <= 0) return false;
            ItemStack next = DurabilityRules.ApplyWear(old, item.MaxDurability,
                points, slot is >= 5 and <= 8, random);
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
                    item.MaxDurability <= 0) continue;
                ItemStack next = DurabilityRules.ApplyWear(old, item.MaxDurability,
                    points, armor: true, random);
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
