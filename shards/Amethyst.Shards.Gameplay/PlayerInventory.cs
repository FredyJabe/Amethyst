using System.Text;
using Amethyst.Shards;

namespace Amethyst;

/// <summary>Simple server-authoritative player inventory. Supports main slots
/// 9..35 and hotbar 36..44; armor/crafting/offhand are reserved for later.
/// All edits are persisted atomically under Gameplay's Shard data directory.</summary>
public sealed class PlayerInventory
{
    private static readonly byte[] Magic = "AMINV003"u8.ToArray();
    private static readonly byte[] LegacyMagic = "AMINV002"u8.ToArray();
    private readonly object _sync = new();
    private readonly ItemStack[] _slots = new ItemStack[46];
    private readonly string? _path;
    private readonly IShardItemRegistry? _items;
    private ItemStack _cursor;
    private int _selected;
    private int _revision;

    public PlayerInventory(string? dataDirectory, Guid player,
        IShardItemRegistry? items = null)
    {
        _items = items;
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
        if (slot is < 9 or > 44 || !plain ||
            !ValidStack(new ItemStack(itemId, count))) return false;
        lock (_sync)
        {
            var original = _slots[slot];
            _slots[slot] = count == 0 ? ItemStack.Empty : new ItemStack(itemId, count);
            try { Persist(); _revision++; }
            catch { _slots[slot] = original; throw; }
            return true;
        }
    }

    /// <summary>Damage the exact currently-held tool after a committed
    /// survival block break. Returns true when a tool was damaged or broke.
    /// Never mutates the stack on a failed or rejected mining attempt.</summary>
    public bool DamageHeld(int expectedItemId, int damage)
    {
        if (damage <= 0) return false;
        lock (_sync)
        {
            int slot = 36 + _selected;
            ItemStack held = _slots[slot];
            if (held.Count != 1 || held.ItemId != expectedItemId ||
                _items is null ||
                !_items.TryGetById(held.ItemId, out var item) ||
                item.MaxDurability <= 0) return false;
            // A tool is removed when its accumulated damage reaches the max.
            int next = held.Damage + damage;
            _slots[slot] = next >= item.MaxDurability ? ItemStack.Empty :
                held with { Damage = next };
            try { Persist(); _revision++; }
            catch { _slots[slot] = held; throw; }
            return true;
        }
    }

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

    /// <returns>Number actually inserted; overflow is not represented as a
    /// ground entity yet, until item entities are implemented.</returns>
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
                if (stack.ItemId != itemId || stack.Damage != 0 ||
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

    /// <summary>Commit a workstation transaction against an inventory snapshot.
    /// Rejects stale snapshots (including concurrent ground-item pickups).
    /// Workstation logic validates all outputs independently of client claims.</summary>
    public bool TryCommitWindow(int expectedRevision, ItemStack[] slots, ItemStack cursor)
    {
        if (slots.Length != _slots.Length || !ValidStack(cursor) ||
            slots.Any(s => !ValidStack(s))) return false;
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
        if (s.Damage < 0) return false;
        if (_items is null)
            return s.Damage == 0 &&
                (s.Count is >= 0 and <= 64 && s.ItemId is >= 0 and <= 1657 &&
                (s.Count == 0) == (s.ItemId == 0));
        if (!_items.IsValidStack(s.ItemId, s.Count)) return false;
        if (s.Count == 0) return s.Damage == 0;
        if (!_items.TryGetById(s.ItemId, out var item)) return false;
        return s.Damage == 0 ||
            (item.MaxDurability > 0 && s.Count == 1 &&
             s.Damage < item.MaxDurability);
    }

    private static bool ValidSlot(int slot) => slot is >= 9 and <= 44;

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
                        _cursor.ItemId != here.ItemId || _cursor.Damage != here.Damage)
                    {
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
                        (here.Count == 0 || (here.ItemId == _cursor.ItemId && here.Damage == _cursor.Damage &&
                            here.Count < MaxStack(_cursor.ItemId))))
                    {
                        _slots[slot] = _cursor with { Count = here.Count + 1 };
                        _cursor = _cursor.Count == 1 ? ItemStack.Empty :
                            _cursor with { Count = _cursor.Count - 1 };
                        changed = true;
                    }
                    else if (_cursor.Count > 0 && here.Count > 0)
                    {
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
                    if (other.ItemId != here.ItemId || other.Damage != here.Damage ||
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
                (_slots[slot], _slots[hotbar]) = (_slots[hotbar], _slots[slot]);
                changed = slot != hotbar;
            }
            if (!changed) return false;
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
        bool legacy = magic.SequenceEqual(LegacyMagic);
        if (!legacy && !magic.SequenceEqual(Magic) ||
            reader.ReadInt32() != _slots.Length)
            throw new InvalidDataException($"Bad player inventory header in {_path}");
        for (int i = 0; i < _slots.Length; i++)
        {
            int item = reader.ReadInt32(), count = reader.ReadInt32();
            int damage = legacy ? 0 : reader.ReadInt32();
            var stack = new ItemStack(item, count) { Damage = damage };
            if (!ValidStack(stack))
                throw new InvalidDataException($"Corrupted inventory slot {i} in {_path}");
            _slots[i] = stack;
        }
        int cursorItem = reader.ReadInt32(), cursorCount = reader.ReadInt32();
        int cursorDamage = legacy ? 0 : reader.ReadInt32();
        var cursorStack = new ItemStack(cursorItem, cursorCount)
            { Damage = cursorDamage };
        if (!ValidStack(cursorStack))
            throw new InvalidDataException($"Corrupted carried item in {_path}");
        _cursor = cursorStack;
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
                foreach (ItemStack slot in _slots)
                {
                    writer.Write(slot.ItemId);
                    writer.Write(slot.Count);
                    writer.Write(slot.Damage);
                }
                writer.Write(_cursor.ItemId);
                writer.Write(_cursor.Count);
                writer.Write(_cursor.Damage);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
