using System.Security.Cryptography;
using System.Text;
using Amethyst.Shards;

namespace Amethyst;

public readonly record struct StationPos(int X, int Y, int Z);

public sealed class StationMenu
{
    public Guid PlayerId { get; }
    public int WindowId { get; }
    public StationKind Kind { get; }
    public StationPos Position { get; }
    public int Revision { get; internal set; }
    internal ItemStack[] CraftingGrid { get; } = new ItemStack[9];

    internal StationMenu(Guid playerId, int windowId, StationKind kind, StationPos position)
    {
        PlayerId = playerId;
        WindowId = windowId;
        Kind = kind;
        Position = position;
    }
}

public sealed class StationState
{
    public StationPos Position { get; }
    public StationKind Kind { get; }
    public ItemStack[] Slots { get; } = new ItemStack[3];
    public int BurnRemaining { get; internal set; }
    public int BurnTotal { get; internal set; }
    public int CookProgress { get; internal set; }
    public int CookTotal { get; internal set; } = 200;

    internal StationState(StationPos position, StationKind kind)
    {
        Position = position;
        Kind = kind;
    }
}

/// <summary>Owned by Gameplay; manages 3x3 crafting menus and shared,
/// persistent furnace-family block entities. All transactions validate
/// client state IDs and never trust client-supplied item contents.</summary>
public sealed class WorkstationManager
{
    private static readonly byte[] Magic = "AMSTAT01"u8.ToArray();
    private readonly object _gate = new();
    private readonly Dictionary<StationPos, StationState> _stations = [];
    private readonly Dictionary<Guid, StationMenu> _menus = [];
    private readonly IShardWorld _world;
    private readonly IShardItemRegistry? _items;
    private readonly string? _path;
    private int _nextWindowId;
    private int _ticks;

    public WorkstationManager(string? directory, string worldPath, IShardWorld world,
        IShardItemRegistry? items = null)
    {
        _world = world;
        _items = items;
        if (directory is null) return;
        string folder = Path.Combine(Path.GetFullPath(directory), "stations");
        Directory.CreateDirectory(folder);
        string key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(worldPath)))).Substring(0, 20);
        _path = Path.Combine(folder, key + ".bin");
        Load();
    }

    private int MaxStack(int id)
    {
        if (id <= 0) return 0;
        if (_items is not null)
            return _items.TryGetById(id, out var item) ? item.MaxStackSize : 0;
        return id <= 1657 ? 64 : 0; // direct legacy unit tests without ShardHost
    }

    private bool ValidStack(ItemStack stack) =>
        _items?.IsValidStack(stack.ItemId, stack.Count) ??
        (stack.ItemId is >= 0 and <= 1657 && stack.Count is >= 0 and <= 64 &&
         (stack.ItemId == 0) == (stack.Count == 0));

    public static StationKind? FromBlockState(ushort state) =>
        state switch
        {
            6866 => StationKind.Crafting,
            >= 6883 and <= 6890 => StationKind.Furnace,
            >= 24111 and <= 24118 => StationKind.Smoker,
            >= 24119 and <= 24126 => StationKind.BlastFurnace,
            _ => null
        };

    public StationMenu Open(Guid playerId, StationPos pos, StationKind kind)
    {
        lock (_gate)
        {
            // Call Close before Open; it returns any abandoned crafting inputs.
            if (_menus.ContainsKey(playerId))
                throw new InvalidOperationException("Close the previous menu before opening another.");
            int id = ++_nextWindowId;
            if (_nextWindowId >= 100) _nextWindowId = 1;
            var menu = new StationMenu(playerId, id, kind, pos);
            if (kind != StationKind.Crafting)
            {
                if (_stations.TryGetValue(pos, out StationState? previous) && previous.Kind != kind)
                    throw new InvalidOperationException("Station kind changed without removal.");
                if (!_stations.ContainsKey(pos))
                {
                    _stations[pos] = new StationState(pos, kind);
                    Persist();
                }
            }
            _menus.Add(playerId, menu);
            return menu;
        }
    }

    public StationMenu? GetMenu(Guid playerId)
    {
        lock (_gate) return _menus.GetValueOrDefault(playerId);
    }

    public ItemStack[] SnapshotStation(StationPos pos)
    {
        lock (_gate) return _stations.TryGetValue(pos, out var state)
            ? (ItemStack[])state.Slots.Clone() : [];
    }

    public StationState? SnapshotState(StationPos pos)
    {
        lock (_gate)
        {
            if (!_stations.TryGetValue(pos, out StationState? state)) return null;
            var copy = new StationState(pos, state.Kind)
            {
                BurnRemaining = state.BurnRemaining,
                BurnTotal = state.BurnTotal,
                CookProgress = state.CookProgress,
                CookTotal = state.CookTotal
            };
            Array.Copy(state.Slots, copy.Slots, 3);
            return copy;
        }
    }

    public (int WindowId, int Revision, ItemStack[] Slots, ItemStack Cursor)? Content(
        Guid playerId, PlayerInventory inventory)
    {
        lock (_gate)
        {
            if (!_menus.TryGetValue(playerId, out StationMenu? menu)) return null;
            var (playerSlots, cursor, _) = inventory.Snapshot();
            int slotsStart = menu.Kind == StationKind.Crafting ? 10 : 3;
            var slots = new ItemStack[slotsStart + 36];
            if (menu.Kind == StationKind.Crafting)
            {
                slots[0] = StationRecipes.MatchCrafting(menu.CraftingGrid)?.Output
                    ?? ItemStack.Empty;
                Array.Copy(menu.CraftingGrid, 0, slots, 1, 9);
            }
            else
            {
                if (!_stations.TryGetValue(menu.Position, out StationState? state)) return null;
                Array.Copy(state.Slots, slots, 3);
            }
            Array.Copy(playerSlots, 9, slots, slotsStart, 27);
            Array.Copy(playerSlots, 36, slots, slotsStart + 27, 9);
            return (menu.WindowId, menu.Revision, slots, cursor);
        }
    }

    public (int Burn, int TotalBurn, int Progress, int TotalProgress) Data(Guid playerId)
    {
        lock (_gate)
        {
            if (_menus.TryGetValue(playerId, out var menu) &&
                _stations.TryGetValue(menu.Position, out var state))
                return (state.BurnRemaining, state.BurnTotal,
                    state.CookProgress, state.CookTotal);
            return (0, 0, 0, 200);
        }
    }

    public ItemStack[] Close(Guid playerId)
    {
        lock (_gate)
        {
            if (!_menus.Remove(playerId, out var menu)) return [];
            // Crafting inputs belong to this menu, not the crafting table block.
            // Return the inputs on close. Furnace slots remain in the block.
            return menu.Kind == StationKind.Crafting
                ? menu.CraftingGrid.Where(s => s.Count > 0).ToArray() : [];
        }
    }

    public (ItemStack[] Contents, Guid[] Viewers) Break(StationPos pos)
    {
        lock (_gate)
        {
            Guid[] viewers = _menus.Values.Where(m => m.Position == pos)
                .Select(m => m.PlayerId).ToArray();
            // Crafting grids are returned to their owners by Close().
            ItemStack[] stacks = [];
            if (_stations.Remove(pos, out var state))
            {
                stacks = state.Slots.Where(s => s.Count > 0).ToArray();
                Persist();
            }
            return (stacks, viewers);
        }
    }

    /// <summary>Process a single standard container input, atomically against
    /// an inventory snapshot. Returns false for invalid/stale actions, so
    /// the caller sends the authoritative window contents to reconcile.</summary>
    public bool Click(Guid playerId, PlayerInventory inventory, InventoryClick click)
    {
        lock (_gate)
        {
            if (!_menus.TryGetValue(playerId, out var menu) ||
                click.Window != menu.WindowId || click.Revision != menu.Revision)
                return false;
            int count = menu.Kind == StationKind.Crafting ? 10 : 3;
            int total = count + 36;
            if (click.Slot < 0 || click.Slot >= total) return false;
            StationState? block = menu.Kind == StationKind.Crafting ? null
                : _stations.GetValueOrDefault(menu.Position);
            if (menu.Kind != StationKind.Crafting && block is null) return false;
            ItemStack[] local = menu.Kind == StationKind.Crafting
                ? (ItemStack[])menu.CraftingGrid.Clone()
                : (ItemStack[])block!.Slots.Clone();
            var (playerSlots, originalCursor, invRevision) = inventory.Snapshot();
            ItemStack cursor = originalCursor;
            int pslot(int menuIndex) => menuIndex < count + 27
                ? menuIndex - count + 9
                : menuIndex - count - 27 + 36;
            ItemStack Get(int index) => index < count
                ? (menu.Kind == StationKind.Crafting
                    ? index == 0
                        ? StationRecipes.MatchCrafting(local)?.Output ?? ItemStack.Empty
                        : local[index - 1]
                    : local[index])
                : playerSlots[pslot(index)];
            bool CanInsert(int index, ItemStack stack) =>
                ValidStack(stack) &&
                (index >= count || menu.Kind == StationKind.Crafting
                || (index != 2 && (stack.Count == 0 ||
                (index == 0 && StationRecipes.TryCook(menu.Kind, stack.ItemId,
                    out _, out _)) ||
                (index == 1 && StationRecipes.FuelTicks(stack.ItemId) > 0))));
            void Put(int index, ItemStack stack)
            {
                if (index >= count) playerSlots[pslot(index)] = stack;
                else if (menu.Kind == StationKind.Crafting) local[index - 1] = stack;
                else local[index] = stack;
            }
            bool changed = false;
            if (menu.Kind != StationKind.Crafting && click.Slot < count &&
                click.Slot != 2 && local[click.Slot].Count > MaxStack(local[click.Slot].ItemId))
                return false;
            bool output = menu.Kind == StationKind.Crafting
                ? click.Slot == 0 : click.Slot == 2;
            if (output)
            {
                ItemStack available = Get(click.Slot);
                if (available.Count == 0) return false;
                bool quick = click.Mode == 1;
                if (!quick && (click.Mode != 0 || click.Button is not (0 or 1)))
                    return false;
                if (quick)
                {
                    if (InsertInventory(playerSlots, available) != 0) return false;
                }
                else
                {
                    if (cursor.Count != 0 &&
                        (cursor.ItemId != available.ItemId ||
                         cursor.Count + available.Count > MaxStack(available.ItemId))) return false;
                    cursor = new ItemStack(available.ItemId, cursor.Count + available.Count);
                }
                if (menu.Kind == StationKind.Crafting)
                {
                    var matched = StationRecipes.MatchCrafting(local);
                    if (matched is null || matched.Value.Output != available) return false;
                    foreach (int index in matched.Value.ConsumedSlots)
                        local[index] = Decrement(local[index], 1);
                }
                else local[2] = Decrement(local[2], available.Count);
                changed = true;
            }
            else if (click.Mode == 0 && click.Button is 0 or 1)
            {
                ItemStack here = Get(click.Slot);
                if (click.Button == 0)
                {
                    if (cursor.Count == 0)
                    {
                        if (here.Count == 0) return false;
                        cursor = here;
                        Put(click.Slot, ItemStack.Empty);
                    }
                    else if (here.Count == 0)
                    {
                        if (!CanInsert(click.Slot, cursor)) return false;
                        Put(click.Slot, cursor);
                        cursor = ItemStack.Empty;
                    }
                    else if (here.ItemId == cursor.ItemId &&
                        here.Count < MaxStack(cursor.ItemId))
                    {
                        if (!CanInsert(click.Slot, cursor)) return false;
                        int moved = Math.Min(MaxStack(cursor.ItemId) - here.Count, cursor.Count);
                        Put(click.Slot, here with { Count = here.Count + moved });
                        cursor = Decrement(cursor, moved);
                    }
                    else
                    {
                        if (!CanInsert(click.Slot, cursor)) return false;
                        Put(click.Slot, cursor);
                        cursor = here;
                    }
                }
                else
                {
                    if (cursor.Count == 0)
                    {
                        if (here.Count == 0) return false;
                        int take = (here.Count + 1) / 2;
                        cursor = new ItemStack(here.ItemId, take);
                        Put(click.Slot, Decrement(here, take));
                    }
                    else if (here.Count == 0 || here.ItemId == cursor.ItemId &&
                        here.Count < MaxStack(cursor.ItemId))
                    {
                        if (!CanInsert(click.Slot, cursor)) return false;
                        Put(click.Slot, new ItemStack(cursor.ItemId, here.Count + 1));
                        cursor = Decrement(cursor, 1);
                    }
                    else
                    {
                        if (!CanInsert(click.Slot, cursor)) return false;
                        Put(click.Slot, cursor);
                        cursor = here;
                    }
                }
                changed = true;
            }
            else if (click.Mode == 1)
            {
                ItemStack here = Get(click.Slot);
                if (here.Count == 0) return false;
                if (click.Slot < count)
                {
                    if (InsertInventory(playerSlots, here) != 0) return false;
                    Put(click.Slot, ItemStack.Empty);
                }
                else
                {
                    if (menu.Kind == StationKind.Crafting) return false;
                    int target = StationRecipes.TryCook(menu.Kind, here.ItemId,
                        out _, out _) ? 0 : StationRecipes.FuelTicks(here.ItemId) > 0 ? 1 : -1;
                    if (target < 0) return false;
                    ItemStack stationStack = local[target];
                    if (stationStack.Count != 0 && stationStack.ItemId != here.ItemId)
                        return false;
                    int moved = Math.Min(MaxStack(here.ItemId) - stationStack.Count, here.Count);
                    if (moved <= 0) return false;
                    local[target] = new ItemStack(here.ItemId, stationStack.Count + moved);
                    Put(click.Slot, Decrement(here, moved));
                }
                changed = true;
            }
            else if (click.Mode == 2 && click.Button is >= 0 and <= 8)
            {
                int hotbarIndex = 36 + click.Button;
                ItemStack here = Get(click.Slot), hotbar = playerSlots[hotbarIndex];
                if (!CanInsert(click.Slot, hotbar)) return false;
                Put(click.Slot, hotbar);
                playerSlots[hotbarIndex] = here;
                changed = true;
            }
            if (!changed) return false;
            if (!inventory.TryCommitWindow(invRevision, playerSlots, cursor))
                return false; // pickup or another event modified inventory meanwhile
            if (menu.Kind == StationKind.Crafting)
                Array.Copy(local, menu.CraftingGrid, 9);
            else
            {
                Array.Copy(local, block!.Slots, 3);
                Persist();
            }
            menu.Revision++;
            // Other viewers of the same furnace must not keep a stale
            // container state ID after this player's transaction.
            if (block is not null)
                foreach (StationMenu observer in _menus.Values)
                    if (!ReferenceEquals(observer, menu) &&
                        observer.Position == menu.Position)
                        observer.Revision++;
            return true;
        }
    }

    private static ItemStack Decrement(ItemStack item, int count) =>
        item.Count <= count ? ItemStack.Empty : item with { Count = item.Count - count };

    private int InsertInventory(ItemStack[] slots, ItemStack incoming)
    {
        int remaining = incoming.Count;
        for (int i = 9; i <= 44 && remaining > 0; i++)
            if (slots[i].ItemId == incoming.ItemId && slots[i].Count > 0 && slots[i].Count < MaxStack(incoming.ItemId))
            {
                int added = Math.Min(remaining, MaxStack(incoming.ItemId) - slots[i].Count);
                slots[i] = slots[i] with { Count = slots[i].Count + added };
                remaining -= added;
            }
        for (int i = 9; i <= 44 && remaining > 0; i++)
            if (slots[i].Count == 0)
            {
                int added = Math.Min(MaxStack(incoming.ItemId), remaining);
                slots[i] = new ItemStack(incoming.ItemId, added);
                remaining -= added;
            }
        return remaining;
    }

    /// <summary>Advance cooking by one server tick. Return stations whose
    /// inventory or progress changed, for UI updates. Does not rely on viewers
    /// being present; furnaces keep working after closing the menu.</summary>
    public StationPos[] Tick()
    {
        lock (_gate)
        {
            var changed = new List<StationPos>();
            bool persist = false;
            foreach (StationState state in _stations.Values)
            {
                // Unloaded/broken stations may be retained in the save;
                // prune them without producing invisible free items.
                int beforeBurn = state.BurnRemaining, beforeCook = state.CookProgress;
                ItemStack oldInput = state.Slots[0];
                ItemStack oldFuel = state.Slots[1];
                ItemStack oldOutput = state.Slots[2];
                if (state.BurnRemaining > 0) state.BurnRemaining--;
                ItemStack result = ItemStack.Empty;
                int duration = 200;
                bool valid = state.Slots[0].Count > 0 &&
                    StationRecipes.TryCook(state.Kind, state.Slots[0].ItemId,
                        out result, out duration) &&
                    (state.Slots[2].Count == 0 ||
                     state.Slots[2].ItemId == result.ItemId &&
                     state.Slots[2].Count + result.Count <= MaxStack(result.ItemId));
                if (valid)
                {
                    state.CookTotal = duration;
                    if (state.BurnRemaining == 0 &&
                        StationRecipes.FuelTicks(state.Slots[1].ItemId) is int fuel && fuel > 0)
                    {
                        state.BurnRemaining = fuel;
                        state.BurnTotal = fuel;
                        state.Slots[1] = Decrement(state.Slots[1], 1);
                        persist = true;
                    }
                    if (state.BurnRemaining > 0)
                    {
                        state.CookProgress++;
                        if (state.CookProgress >= duration)
                        {
                            state.CookProgress = 0;
                            state.Slots[0] = Decrement(state.Slots[0], 1);
                            state.Slots[2] = new ItemStack(result.ItemId,
                                state.Slots[2].Count + result.Count);
                            persist = true;
                        }
                    }
                }
                else state.CookProgress = 0;
                bool inventoryChanged = oldInput != state.Slots[0] ||
                    oldFuel != state.Slots[1] || oldOutput != state.Slots[2];
                if (inventoryChanged)
                    foreach (StationMenu viewer in _menus.Values)
                        if (viewer.Position == state.Position) viewer.Revision++;
                if (state.CookProgress != beforeCook ||
                    state.BurnRemaining != beforeBurn || inventoryChanged)
                    changed.Add(state.Position);
            }
            // Do not rewrite the station file every second on an empty or
            // completely idle world. Active station progress is checkpointed
            // once per second; fuel/output mutations persist immediately.
            if (persist || (changed.Count > 0 && ++_ticks % 20 == 0)) Persist();
            return changed.ToArray();
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        using var stream = File.OpenRead(_path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic) ||
            reader.ReadInt64() != _world.Seed || reader.ReadString() != _world.DimensionId)
            throw new InvalidDataException("Invalid station store header/world identity.");
        int count = reader.ReadInt32();
        if (count is < 0 or > 8192) throw new InvalidDataException("Invalid station count.");
        for (int i = 0; i < count; i++)
        {
            var pos = new StationPos(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            StationKind kind = (StationKind)reader.ReadByte();
            if (kind is not (StationKind.Furnace or StationKind.Smoker or StationKind.BlastFurnace))
                throw new InvalidDataException("Invalid saved station kind.");
            var state = new StationState(pos, kind)
            {
                BurnRemaining = reader.ReadInt32(),
                BurnTotal = reader.ReadInt32(),
                CookProgress = reader.ReadInt32(),
                CookTotal = reader.ReadInt32()
            };
            for (int slot = 0; slot < 3; slot++)
            {
                int itemId = reader.ReadInt32(), amount = reader.ReadInt32();
                if (!ValidStack(new ItemStack(itemId, amount)))
                    throw new InvalidDataException("Invalid persisted station inventory.");
                state.Slots[slot] = new ItemStack(itemId, amount);
            }
            if (state.BurnRemaining is < 0 or > 100000 || state.BurnTotal is < 0 or > 100000 ||
                state.CookProgress is < 0 or > 10000 || state.CookTotal is < 0 or > 10000)
                throw new InvalidDataException("Invalid smelting progress.");
            _stations.Add(pos, state);
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Unexpected data after station store.");
    }

    public void Save()
    {
        lock (_gate) Persist();
    }

    private void Persist()
    {
        if (_path is null) return;
        string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(_world.Seed);
                writer.Write(_world.DimensionId);
                writer.Write(_stations.Count);
                foreach (StationState state in _stations.Values.OrderBy(x => x.Position.X)
                    .ThenBy(x => x.Position.Y).ThenBy(x => x.Position.Z))
                {
                    writer.Write(state.Position.X);
                    writer.Write(state.Position.Y);
                    writer.Write(state.Position.Z);
                    writer.Write((byte)state.Kind);
                    writer.Write(state.BurnRemaining);
                    writer.Write(state.BurnTotal);
                    writer.Write(state.CookProgress);
                    writer.Write(state.CookTotal);
                    foreach (ItemStack stack in state.Slots)
                    {
                        writer.Write(stack.ItemId);
                        writer.Write(stack.Count);
                    }
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
