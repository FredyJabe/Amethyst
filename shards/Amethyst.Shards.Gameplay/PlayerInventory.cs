using System.Text;
using Amethyst.Shards;

namespace Amethyst;

/// <summary>Simple server-authoritative player inventory. Supports main slots
/// 9..35 and hotbar 36..44; armor/crafting/offhand are reserved for later.
/// All edits are persisted atomically under Gameplay's Shard data directory.</summary>
public sealed partial class PlayerInventory : IShardPickupInventory
{
    private static readonly byte[] Magic = "AMINV006"u8.ToArray();
    private static readonly byte[] EnchantmentMagic = "AMINV005"u8.ToArray();
    private static readonly byte[] PreviousMagic = "AMINV004"u8.ToArray();
    private static readonly byte[] DamageMagic = "AMINV003"u8.ToArray();
    private static readonly byte[] LegacyMagic = "AMINV002"u8.ToArray();
    private readonly object _sync = new();
    private readonly ItemStack[] _slots = new ItemStack[46];
    private readonly string? _path;
    private readonly IShardItemRegistry? _items;
    private readonly EnchantmentDurability? _durabilitySkills;
    internal IShardItemRegistry? ItemRegistry => _items;
    internal bool PreventsDurabilityWear(ItemStack item, bool armor, Random rng) =>
        _durabilitySkills?.PreventsWear(item, armor, rng) ?? false;
    private ItemStack _cursor;
    private int _selected;
    private int _revision;

    public PlayerInventory(string? dataDirectory, Guid player,
        IShardItemRegistry? items = null, IShardEnchantmentRegistry? enchantments = null,
        IShardSkillEngine? skills = null)
    {
        _items = items;
        if (items is not null && enchantments is not null && skills is not null)
            _durabilitySkills = new EnchantmentDurability(items, enchantments, skills);
        if (dataDirectory is null) return;
        string directory = Path.Combine(Path.GetFullPath(dataDirectory), "inventories");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, player.ToString("N") + ".bin");
        Load();
    }

    public (ItemStack[] Slots, ItemStack Cursor, int Revision) Snapshot()
    {
        lock (_sync) return ((ItemStack[])_slots.Clone(), _cursor, _revision);
    }

    public void SelectHotbar(int index)
    {
        if (index is < 0 or > 8) return;
        lock (_sync) _selected = index;
    }

    public int SelectedSlot { get { lock (_sync) return 36 + _selected; } }

    public ItemStack Held()
    {
        lock (_sync) return _slots[36 + _selected];
    }

    public bool TryCreativeSet(int slot, int itemId, int count, bool plain)
    {
        if (!ValidSlot(slot) || !plain ||
            !ValidSlotStack(slot, new ItemStack(itemId, count))) return false;
        lock (_sync)
        {
            var original = _slots[slot];
            _slots[slot] = count == 0 ? ItemStack.Empty : new ItemStack(itemId, count);
            try { Persist(); _revision++; }
            catch { _slots[slot] = original; throw; }
            return true;
        }
    }

    /// <summary>Wear on the selected tool, honoring Unbreaking
    /// and Unbreakable through the shared durability engine.</summary>
    public bool DamageHeld(int expectedItemId, int damage) =>
        DamageSlot(SelectedSlot, expectedItemId, damage);

    /// <summary>Remove one placed item, if held. Creative mode never consumes.</summary>
    public bool ConsumeHeld(bool creative, int expectedItem)
    {
        if (creative) return true;
        lock (_sync)
        {
            int slot = 36 + _selected;
            ItemStack current = _slots[slot];
            if (current.ItemId != expectedItem || current.Count == 0) return false;
            _slots[slot] = current.Count == 1 ? ItemStack.Empty :
                current with { Count = current.Count - 1 };
            try { Persist(); _revision++; }
            catch { _slots[slot] = current; throw; }
            return true;
        }
    }

    /// <summary>Remove a precisely identified held stack after a physical
    /// ground-item spawn has succeeded. Rejects stale hotbar changes and
    /// never consumes more items than are currently held.</summary>
    public bool TryTakeHeld(int expectedItemId, int count)
    {
        if (expectedItemId <= 0 || count <= 0) return false;
        lock (_sync)
        {
            int slot = 36 + _selected;
            ItemStack current = _slots[slot];
            if (current.ItemId != expectedItemId || current.Count < count)
                return false;
            _slots[slot] = current.Count == count
                ? ItemStack.Empty : current with { Count = current.Count - count };
            try { Persist(); _revision++; }
            catch { _slots[slot] = current; throw; }
            return true;
        }
    }

    /// <returns>Number actually inserted; excess remains uncollected.</returns>
    public int Give(int itemId, int count)
    {
        int maximum = MaxStack(itemId);
        if (maximum == 0 || count <= 0) return 0;
        lock (_sync)
        {
            var backup = (ItemStack[])_slots.Clone();
            int remaining = count;
            foreach (int index in Enumerable.Range(36, 9).Concat(Enumerable.Range(9, 27)))
            {
                ItemStack stack = _slots[index];
                if (!SameItemData(stack, new ItemStack(itemId, 1)) ||
                    stack.Count == 0 || stack.Count >= maximum) continue;
                int delta = Math.Min(maximum - stack.Count, remaining);
                _slots[index] = stack with { Count = stack.Count + delta };
                remaining -= delta;
                if (remaining == 0) break;
            }
            if (remaining > 0)
            foreach (int index in Enumerable.Range(36, 9).Concat(Enumerable.Range(9, 27)))
            {
                if (_slots[index].Count != 0) continue;
                int delta = Math.Min(maximum, remaining);
                _slots[index] = new ItemStack(itemId, delta);
                remaining -= delta;
                if (remaining == 0) break;
            }
            if (remaining == count) return 0;
            try { Persist(); _revision++; }
            catch { Array.Copy(backup, _slots, _slots.Length); throw; }
            return count - remaining;
        }
    }

    /// <summary>Atomically convert one held bucket into a filled bucket.
    /// When buckets are stacked, insert the filled result elsewhere in the
    /// inventory; refuse the interaction if the result has nowhere to go.
    /// This prevents item duplication or loss when the inventory is full.</summary>
    public bool TryConvertHeld(int expectedItem, int outputItem)
    {
        if (expectedItem <= 0 || MaxStack(outputItem) < 1) return false;
        lock (_sync)
        {
            int selected = 36 + _selected;
            ItemStack held = _slots[selected];
            if (held.ItemId != expectedItem || held.Count < 1) return false;
            var before = (ItemStack[])_slots.Clone();
            if (held.Count == 1)
                _slots[selected] = new ItemStack(outputItem, 1);
            else
            {
                int destination = -1;
                foreach (int slot in Enumerable.Range(36, 9).Concat(Enumerable.Range(9, 27)))
                {
                    ItemStack other = _slots[slot];
                    if (slot != selected && other.ItemId == outputItem &&
                        other.Count > 0 && other.Count < MaxStack(outputItem))
                    {
                        destination = slot;
                        break;
                    }
                    if (slot != selected && destination < 0 && other.Count == 0)
                        destination = slot;
                }
                if (destination < 0) return false;
                ItemStack previous = _slots[destination];
                _slots[selected] = held with { Count = held.Count - 1 };
                _slots[destination] = previous.Count == 0
                    ? new ItemStack(outputItem, 1)
                    : previous with { Count = previous.Count + 1 };
            }
            try { Persist(); _revision++; }
            catch { Array.Copy(before, _slots, _slots.Length); throw; }
            return true;
        }
    }

    /// <summary>Commit a workstation transaction against an inventory snapshot.
    /// Rejects stale snapshots (including concurrent ground-item pickups).
    /// Workstation logic validates all outputs independently of client claims.</summary>
    public bool TryCommitWindow(int expectedRevision, ItemStack[] slots, ItemStack cursor)
    {
        if (slots.Length != _slots.Length || !ValidStack(cursor) ||
            slots.Where((stack, index) => !ValidSlotStack(index, stack)).Any())
            return false;
        lock (_sync)
        {
            if (expectedRevision != _revision) return false;
            ItemStack[] original = (ItemStack[])_slots.Clone();
            ItemStack oldCursor = _cursor;
            Array.Copy(slots, _slots, _slots.Length);
            _cursor = cursor;
            try { Persist(); _revision++; }
            catch
            {
                Array.Copy(original, _slots, _slots.Length);
                _cursor = oldCursor;
                throw;
            }
            return true;
        }
    }

    private int MaxStack(int itemId)
    {
        if (itemId <= 0) return 0;
        if (_items is not null)
            return _items.TryGetById(itemId, out var item) ? item.MaxStackSize : 0;
        // Direct unit-test construction can omit a registry, but the live
        // Gameplay Shard always obtains IShardItemRegistry from the Items Shard.
        return itemId <= 1657 ? 64 : 0;
    }

    private bool ValidStack(ItemStack s)
    {
        if (!ItemStackComponents.IsValid(s.ComponentData)) return false;
        if (s.Count == 0 && !string.IsNullOrEmpty(s.ComponentData)) return false;
        if (s.Damage < 0 || s.Unbreaking is < 0 or > 3 ||
            s.RepairCost is < 0 or > 1_000_000) return false;
        bool enchanted = !string.IsNullOrEmpty(s.EnchantmentData);
        if (enchanted)
        {
            string[] entries = (s.EnchantmentData ?? "").Split(';');
            if (entries.Length > 32 || entries.Any(e => e.Length > 128) ||
                !entries.SequenceEqual(entries.Order(StringComparer.Ordinal)))
                return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string entry in entries)
            {
                int equal = entry.LastIndexOf('=');
                if (equal <= 0 || !int.TryParse(entry.AsSpan(equal + 1),
                        out int level) || level is < 1 or > 255)
                    return false;
                string id = entry[..equal];
                if (!id.StartsWith("minecraft:", StringComparison.Ordinal) ||
                    id is "minecraft:unbreaking" or "minecraft:mending" ||
                    !ids.Add(id)) return false;
                try { _ = VanillaRegistries.IndexOf("minecraft:enchantment", id); }
                catch (InvalidDataException) { return false; }
            }
        }
        bool extra = s.Damage > 0 || s.Unbreaking > 0 || s.Mending ||
            s.Unbreakable || s.RepairCost > 0 || enchanted;
        if (_items is null)
            return !extra && s.Count is >= 0 and <= 64 &&
                s.ItemId is >= 0 and <= 1657 &&
                (s.Count == 0) == (s.ItemId == 0);
        if (!_items.IsValidStack(s.ItemId, s.Count)) return false;
        if (s.Count == 0) return !extra;
        if (!_items.TryGetById(s.ItemId, out var item)) return false;
        if (!extra) return true;
        if (s.Count != 1) return false;
        if (item.MaxDurability > 0) return s.Damage < item.MaxDurability;
        // Books are allowed to hold enchantments without physical wear.
        return enchanted && s.Damage == 0 && s.Unbreaking == 0 &&
            !s.Mending && !s.Unbreakable && s.RepairCost == 0 &&
            item.Identifier is "minecraft:book" or "minecraft:enchanted_book";
    }

    private bool ValidSlotStack(int slot, ItemStack item)
    {
        if (!ValidStack(item)) return false;
        if (item.Count == 0) return true;
        if (slot is < 5 or > 45) return false;
        if (slot is >= 9 and <= 45) return true;
        if (_items is null || !_items.TryGetById(item.ItemId, out var definition))
            return false;
        string name = definition.Identifier;
        return slot switch
        {
            5 => name.EndsWith("_helmet", StringComparison.Ordinal),
            6 => name.EndsWith("_chestplate", StringComparison.Ordinal) ||
                 name == "minecraft:elytra",
            7 => name.EndsWith("_leggings", StringComparison.Ordinal),
            8 => name.EndsWith("_boots", StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool ValidSlot(int slot) => slot is >= 5 and <= 45;

    private static bool SameItemData(ItemStack a, ItemStack b) =>
        a.ItemId == b.ItemId && a.Damage == b.Damage &&
        a.Unbreaking == b.Unbreaking && a.Mending == b.Mending &&
        a.Unbreakable == b.Unbreakable && a.RepairCost == b.RepairCost &&
        string.Equals(a.EnchantmentData ?? "", b.EnchantmentData ?? "",
            StringComparison.Ordinal) &&
        string.Equals(a.ComponentData ?? "", b.ComponentData ?? "",
            StringComparison.Ordinal);

    /// <summary>Execute server-side mouse clicks, hotbar swaps and shift-clicks.
    /// Client-provided item hashes are never trusted to mint items.</summary>
    public bool ApplyClick(InventoryClick click)
    {
        lock (_sync)
        {
            if (click.Window != 0 || click.Revision != _revision ||
                !ValidSlot(click.Slot)) return false;
            ItemStack[] backup = (ItemStack[])_slots.Clone();
            ItemStack cursor = _cursor;
            bool changed = false;
            int slot = click.Slot;
            ItemStack here = _slots[slot];
            if (click.Mode == 0 && click.Button is 0 or 1)
            {
                if (click.Button == 0)
                {
                    if (_cursor.Count == 0 || here.Count == 0 ||
                        !SameItemData(_cursor, here))
                    {
                        if (!ValidSlotStack(slot, _cursor)) return false;
                        (_slots[slot], _cursor) = (_cursor, here);
                        changed = true;
                    }
                    else if (here.Count < MaxStack(here.ItemId))
                    {
                        int moved = Math.Min(_cursor.Count, MaxStack(here.ItemId) - here.Count);
                        _slots[slot] = here with { Count = here.Count + moved };
                        _cursor = moved == _cursor.Count ? ItemStack.Empty :
                            _cursor with { Count = _cursor.Count - moved };
                        changed = moved != 0;
                    }
                }
                else
                {
                    if (_cursor.Count == 0 && here.Count > 0)
                    {
                        int take = (here.Count + 1) / 2;
                        _cursor = here with { Count = take };
                        _slots[slot] = here.Count == take ? ItemStack.Empty :
                            here with { Count = here.Count - take };
                        changed = true;
                    }
                    else if (_cursor.Count > 0 &&
                        (here.Count == 0 || (SameItemData(here, _cursor) &&
                            here.Count < MaxStack(_cursor.ItemId))))
                    {
                        if (!ValidSlotStack(slot, _cursor with { Count = here.Count + 1 }))
                            return false;
                        _slots[slot] = _cursor with { Count = here.Count + 1 };
                        _cursor = _cursor.Count == 1 ? ItemStack.Empty :
                            _cursor with { Count = _cursor.Count - 1 };
                        changed = true;
                    }
                    else if (_cursor.Count > 0 && here.Count > 0)
                    {
                        if (!ValidSlotStack(slot, _cursor)) return false;
                        (_slots[slot], _cursor) = (_cursor, here);
                        changed = true;
                    }
                }
            }
            else if (click.Mode == 1 && click.Button is 0 or 1 && here.Count > 0)
            {
                int low = slot >= 36 ? 9 : 36;
                int high = slot >= 36 ? 35 : 44;
                int remaining = here.Count;
                for (int i = low; i <= high && remaining > 0; i++)
                {
                    ItemStack other = _slots[i];
                    if (!SameItemData(other, here) ||
                        other.Count == 0 || other.Count >= MaxStack(here.ItemId)) continue;
                    int moved = Math.Min(remaining, MaxStack(here.ItemId) - other.Count);
                    _slots[i] = other with { Count = other.Count + moved };
                    remaining -= moved;
                }
                for (int i = low; i <= high && remaining > 0; i++)
                {
                    if (_slots[i].Count != 0) continue;
                    int moved = Math.Min(MaxStack(here.ItemId), remaining);
                    _slots[i] = here with { Count = moved };
                    remaining -= moved;
                }
                if (remaining != here.Count)
                {
                    _slots[slot] = remaining == 0 ? ItemStack.Empty :
                        here with { Count = remaining };
                    changed = true;
                }
            }
            else if (click.Mode == 2 && click.Button is >= 0 and <= 8)
            {
                int hotbar = 36 + click.Button;
                if (!ValidSlotStack(slot, _slots[hotbar])) return false;
                (_slots[slot], _slots[hotbar]) = (_slots[hotbar], _slots[slot]);
                changed = slot != hotbar;
            }
            if (!changed) return false;
            if (!ValidSlotStack(slot, _slots[slot]) || !ValidStack(_cursor))
            {
                Array.Copy(backup, _slots, _slots.Length);
                _cursor = cursor;
                return false;
            }
            try { Persist(); _revision++; }
            catch
            {
                Array.Copy(backup, _slots, _slots.Length);
                _cursor = cursor;
                throw;
            }
            return true;
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        using var stream = File.OpenRead(_path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        byte[] magic = reader.ReadBytes(Magic.Length);
        bool oldPlain = magic.SequenceEqual(LegacyMagic);
        bool oldDamage = magic.SequenceEqual(DamageMagic);
        bool oldV4 = magic.SequenceEqual(PreviousMagic);
        bool oldV5 = magic.SequenceEqual(EnchantmentMagic);
        if (!oldPlain && !oldDamage && !oldV4 && !oldV5 &&
            !magic.SequenceEqual(Magic) ||
            reader.ReadInt32() != _slots.Length)
            throw new InvalidDataException($"Bad player inventory header in {_path}");
        ItemStack ReadStack()
        {
            int item = reader.ReadInt32(), count = reader.ReadInt32();
            int damage = oldPlain ? 0 : reader.ReadInt32();
            int unbreaking = oldPlain || oldDamage ? 0 : reader.ReadByte();
            bool mending = !oldPlain && !oldDamage && reader.ReadBoolean();
            bool unbreakable = !oldPlain && !oldDamage && reader.ReadBoolean();
            int cost = oldPlain || oldDamage ? 0 : reader.ReadInt32();
            string enchantments = oldPlain || oldDamage || oldV4 ? "" : reader.ReadString();
            string components = oldPlain || oldDamage || oldV4 || oldV5 ? "" :
                reader.ReadString();
            if (enchantments.Length > 4096 ||
                components.Length > ItemStackComponents.MaximumCharacters)
                throw new InvalidDataException("Oversized inventory component.");
            return new ItemStack(item, count)
            {
                Damage = damage, Unbreaking = unbreaking, Mending = mending,
                Unbreakable = unbreakable, RepairCost = cost,
                EnchantmentData = enchantments.Length == 0 ? null : enchantments,
                ComponentData = components.Length == 0 ? null : components
            };
        }
        for (int i = 0; i < _slots.Length; i++)
        {
            ItemStack item = ReadStack();
            if (!ValidSlotStack(i, item))
                throw new InvalidDataException($"Corrupted inventory slot {i} in {_path}");
            _slots[i] = item;
        }
        ItemStack cursor = ReadStack();
        if (!ValidStack(cursor))
            throw new InvalidDataException($"Corrupted carried item in {_path}");
        _cursor = cursor;
        if (stream.Position != stream.Length)
            throw new InvalidDataException($"Unexpected inventory data in {_path}");
    }

    // Client cursor contents persist as a separate, authoritative carried stack
    // rather than disappearing when a GUI is closed or the player disconnects.

    private void Persist()
    {
        if (_path is null) return;
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(_slots.Length);
                static void WriteStack(BinaryWriter output, ItemStack slot)
                {
                    output.Write(slot.ItemId);
                    output.Write(slot.Count);
                    output.Write(slot.Damage);
                    output.Write((byte)slot.Unbreaking);
                    output.Write(slot.Mending);
                    output.Write(slot.Unbreakable);
                    output.Write(slot.RepairCost);
                    output.Write(slot.EnchantmentData ?? "");
                    output.Write(slot.ComponentData ?? "");
                }
                foreach (ItemStack slot in _slots) WriteStack(writer, slot);
                WriteStack(writer, _cursor);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
