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
    internal ItemStack[] RepairGrid { get; } = new ItemStack[2];

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
    private static readonly byte[] Magic = "AMSTAT03"u8.ToArray();
    private static readonly byte[] DamageMagic = "AMSTAT02"u8.ToArray();
    private static readonly byte[] LegacyMagic = "AMSTAT01"u8.ToArray();
    private readonly object _gate = new();
    private readonly Dictionary<StationPos, StationState> _stations = [];
    private readonly Dictionary<Guid, StationMenu> _menus = [];
    private readonly IShardWorld _world;
    private readonly IShardItemRegistry? _items;
    private readonly IShardRecipeRegistry _recipes;
    private readonly string? _path;
    private int _nextWindowId;
    private int _ticks;

    public WorkstationManager(string? directory, string worldPath, IShardWorld world,
        IShardItemRegistry? items, IShardRecipeRegistry recipes)
    {
        _world = world;
        _items = items;
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        if (directory is null) return;
        string folder = Path.Combine(Path.GetFullPath(directory), "stations");
        Directory.CreateDirectory(folder);
        string key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(worldPath)))).Substring(0, 20);
        _path = Path.Combine(folder, key + ".bin");
        Load();
    }

    private static bool IsRepair(StationKind kind) =>
        kind is StationKind.Anvil or StationKind.Grindstone;

    private readonly record struct RepairPreview(ItemStack Result,
        int MaterialConsumed, int LevelCost);

    private RepairPreview? MatchRepair(StationKind kind, ItemStack[] grid)
    {
        if (!IsRepair(kind) || grid.Length != 2) return null;
        if (kind == StationKind.Anvil)
        {
            if (!DurabilityRules.TryAnvil(grid[0], grid[1], _items,
                out ItemStack result, out int consumed, out int levelCost) ||
                levelCost > 39) return null;
            return new RepairPreview(result, consumed, levelCost);
        }
        if (!DurabilityRules.TryGrindstone(grid[0], grid[1], _items,
            out ItemStack output)) return null;
        return new RepairPreview(output, grid[1].Count > 0 ? 1 : 0, 0);
    }

    private CraftingMatch? MatchCrafting(ItemStack[] grid)
    {
        int[] occupied = Enumerable.Range(0, grid.Length)
            .Where(i => grid[i].Count > 0).ToArray();
        if (occupied.Length == 2 &&
            DurabilityRules.TryCombine(grid[occupied[0]], grid[occupied[1]],
                _items, out ItemStack repaired))
            return new CraftingMatch(repaired, occupied);
        ShardRecipeStack[] inputs = grid.Select(s =>
            new ShardRecipeStack(s.ItemId, s.Count)).ToArray();
        if (!_recipes.TryMatchCrafting(inputs, out var match)) return null;
        return new CraftingMatch(new ItemStack(match.Result.ItemId, match.Result.Count),
            match.ConsumedSlots);
    }

    private bool TryCook(StationKind kind, int ingredientId, out ItemStack result,
        out int durationTicks)
    {
        result = ItemStack.Empty;
        durationTicks = kind is StationKind.Furnace ? 200 : 100;
        if (kind == StationKind.Crafting) return false;
        ShardCookingStation station = kind switch
        {
            StationKind.Furnace => ShardCookingStation.Furnace,
            StationKind.Smoker => ShardCookingStation.Smoker,
            StationKind.BlastFurnace => ShardCookingStation.BlastFurnace,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (!_recipes.TryGetCookingRecipe(station, ingredientId, out var match))
            return false;
        result = new ItemStack(match.Result.ItemId, match.Result.Count);
        durationTicks = match.CookingTicks;
        return true;
    }

    private int MaxStack(int id)
    {
        if (id <= 0) return 0;
        if (_items is not null)
            return _items.TryGetById(id, out var item) ? item.MaxStackSize : 0;
        return id <= 1657 ? 64 : 0; // direct legacy unit tests without ShardHost
    }

    private bool ValidStack(ItemStack stack)
    {
        if (stack.Damage < 0 || stack.Unbreaking is < 0 or > 3 ||
            stack.RepairCost is < 0 or > 1_000_000) return false;
        bool patched = stack.Damage > 0 || stack.Unbreaking > 0 ||
            stack.Mending || stack.Unbreakable || stack.RepairCost > 0;
        if (_items is null)
            return !patched && stack.ItemId is >= 0 and <= 1657 &&
                stack.Count is >= 0 and <= 64 &&
                (stack.ItemId == 0) == (stack.Count == 0);
        if (!_items.IsValidStack(stack.ItemId, stack.Count)) return false;
        if (stack.Count == 0) return !patched;
        if (!_items.TryGetById(stack.ItemId, out var item)) return false;
        return !patched || stack.Count == 1 &&
            item.MaxDurability > stack.Damage && item.MaxDurability > 0;
    }

    private static bool SameItemData(ItemStack a, ItemStack b) =>
        a.ItemId == b.ItemId && a.Damage == b.Damage &&
        a.Unbreaking == b.Unbreaking && a.Mending == b.Mending &&
        a.Unbreakable == b.Unbreakable && a.RepairCost == b.RepairCost;

    public static StationKind? FromBlockState(ushort state) =>
        state switch
        {
            6866 => StationKind.Crafting,
            >= 6883 and <= 6890 => StationKind.Furnace,
            >= 24111 and <= 24118 => StationKind.Smoker,
            >= 24119 and <= 24126 => StationKind.BlastFurnace,
            >= 12962 and <= 12973 => StationKind.Anvil,
            >= 24129 and <= 24140 => StationKind.Grindstone,
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
            if (kind != StationKind.Crafting && !IsRepair(kind))
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
                slots[0] = MatchCrafting(menu.CraftingGrid)?.Output
                    ?? ItemStack.Empty;
                Array.Copy(menu.CraftingGrid, 0, slots, 1, 9);
            }
            else if (IsRepair(menu.Kind))
            {
                slots[0] = menu.RepairGrid[0];
                slots[1] = menu.RepairGrid[1];
                slots[2] = MatchRepair(menu.Kind, menu.RepairGrid)?.Result
                    ?? ItemStack.Empty;
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
            if (_menus.TryGetValue(playerId, out var menu))
            {
                if (menu.Kind == StationKind.Anvil)
                    return (MatchRepair(menu.Kind, menu.RepairGrid)?.LevelCost ?? 0,
                        0, 0, 0);
                if (_stations.TryGetValue(menu.Position, out var state))
                    return (state.BurnRemaining, state.BurnTotal,
                        state.CookProgress, state.CookTotal);
            }
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
                ? menu.CraftingGrid.Where(s => s.Count > 0).ToArray() :
                IsRepair(menu.Kind)
                    ? menu.RepairGrid.Where(s => s.Count > 0).ToArray() : [];
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
    public bool Click(Guid playerId, PlayerInventory inventory, InventoryClick click,
        PlayerExperience? experience = null, bool creative = false)
    {
        lock (_gate)
        {
            if (!_menus.TryGetValue(playerId, out var menu) ||
                click.Window != menu.WindowId || click.Revision != menu.Revision)
                return false;
            bool repair = IsRepair(menu.Kind);
            int count = menu.Kind == StationKind.Crafting ? 10 : 3;
            int total = count + 36;
            if (click.Slot < 0 || click.Slot >= total) return false;
            StationState? block = menu.Kind == StationKind.Crafting || repair
                ? null : _stations.GetValueOrDefault(menu.Position);
            if (menu.Kind != StationKind.Crafting && !repair && block is null)
                return false;
            ItemStack[] local = menu.Kind == StationKind.Crafting
                ? (ItemStack[])menu.CraftingGrid.Clone()
                : repair ? (ItemStack[])menu.RepairGrid.Clone()
                : (ItemStack[])block!.Slots.Clone();
            var (playerSlots, originalCursor, invRevision) = inventory.Snapshot();
            ItemStack cursor = originalCursor;
            int pslot(int menuIndex) => menuIndex < count + 27
                ? menuIndex - count + 9
                : menuIndex - count - 27 + 36;
            ItemStack Get(int index) => index < count
                ? (menu.Kind == StationKind.Crafting
                    ? index == 0
                        ? MatchCrafting(local)?.Output ?? ItemStack.Empty
                        : local[index - 1]
                    : repair && index == 2
                        ? MatchRepair(menu.Kind, local)?.Result ?? ItemStack.Empty
                        : local[index])
                : playerSlots[pslot(index)];
            bool CanInsert(int index, ItemStack stack) =>
                ValidStack(stack) &&
                (index >= count || menu.Kind == StationKind.Crafting
                || (repair && index < 2)
                || (index != 2 && (stack.Count == 0 ||
                (index == 0 && TryCook(menu.Kind, stack.ItemId,
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
            int xpCost = 0;
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
                        (!SameItemData(cursor, available) ||
                         cursor.Count + available.Count > MaxStack(available.ItemId))) return false;
                    cursor = available with { Count = cursor.Count + available.Count };
                }
                if (menu.Kind == StationKind.Crafting)
                {
                    var matched = MatchCrafting(local);
                    if (matched is null || matched.Value.Output != available) return false;
                    foreach (int index in matched.Value.ConsumedSlots)
                        local[index] = Decrement(local[index], 1);
                }
                else if (repair)
                {
                    RepairPreview? match = MatchRepair(menu.Kind, local);
                    if (match is not { } preview ||
                        preview.Result != available) return false;
                    local[0] = Decrement(local[0], 1);
                    local[1] = Decrement(local[1], preview.MaterialConsumed);
                    xpCost = menu.Kind == StationKind.Anvil && !creative
                        ? preview.LevelCost : 0;
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
                    else if (SameItemData(here, cursor) &&
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
                        cursor = here with { Count = take };
                        Put(click.Slot, Decrement(here, take));
                    }
                    else if (here.Count == 0 ||
                        SameItemData(here, cursor) &&
                        here.Count < MaxStack(cursor.ItemId))
                    {
                        if (!CanInsert(click.Slot, cursor)) return false;
                        Put(click.Slot, cursor with { Count = here.Count + 1 });
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
                    int target = repair ?
                        local[0].Count == 0 ? 0 : local[1].Count == 0 ? 1 : -1 :
                        TryCook(menu.Kind, here.ItemId,
                        out _, out _) ? 0 : StationRecipes.FuelTicks(here.ItemId) > 0 ? 1 : -1;
                    if (target < 0) return false;
                    ItemStack stationStack = local[target];
                    if (stationStack.Count != 0 &&
                        (!SameItemData(stationStack, here)))
                        return false;
                    int moved = Math.Min(MaxStack(here.ItemId) - stationStack.Count, here.Count);
                    if (moved <= 0) return false;
                    local[target] = here with { Count = stationStack.Count + moved };
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
            bool committed = xpCost == 0 || creative
                ? inventory.TryCommitWindow(invRevision, playerSlots, cursor)
                : experience is not null && experience.TryPayLevels(xpCost,
                    () => inventory.TryCommitWindow(invRevision, playerSlots, cursor));
            if (!committed) return false;
            if (menu.Kind == StationKind.Crafting)
                Array.Copy(local, menu.CraftingGrid, 9);
            else if (repair)
                Array.Copy(local, menu.RepairGrid, 2);
            else
            {
                // Recipe duration/output may differ between configured inputs.
                // Never carry cooking progress across a different ingredient,
                // which could otherwise produce a short recipe's result early.
                int previousIngredient = block!.Slots[0].ItemId;
                Array.Copy(local, block.Slots, 3);
                if (block.Slots[0].ItemId != previousIngredient)
                    block.CookProgress = 0;
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
            if (SameItemData(slots[i], incoming) &&
                slots[i].Count > 0 && slots[i].Count < MaxStack(incoming.ItemId))
            {
                int added = Math.Min(remaining, MaxStack(incoming.ItemId) - slots[i].Count);
                slots[i] = slots[i] with { Count = slots[i].Count + added };
                remaining -= added;
            }
        for (int i = 9; i <= 44 && remaining > 0; i++)
            if (slots[i].Count == 0)
            {
                int added = Math.Min(MaxStack(incoming.ItemId), remaining);
                slots[i] = incoming with { Count = added };
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
                    TryCook(state.Kind, state.Slots[0].ItemId,
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
                            state.Slots[2] = result with
                            { Count = state.Slots[2].Count + result.Count };
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
        byte[] magic = reader.ReadBytes(Magic.Length);
        bool legacy = magic.SequenceEqual(LegacyMagic);
        bool damageOnly = magic.SequenceEqual(DamageMagic);
        if (!legacy && !damageOnly && !magic.SequenceEqual(Magic) ||
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
                int damage = legacy ? 0 : reader.ReadInt32();
                int unbreaking = legacy || damageOnly ? 0 : reader.ReadByte();
                bool mending = !legacy && !damageOnly && reader.ReadBoolean();
                bool unbreakable = !legacy && !damageOnly && reader.ReadBoolean();
                int repairCost = legacy || damageOnly ? 0 : reader.ReadInt32();
                var item = new ItemStack(itemId, amount)
                {
                    Damage = damage, Unbreaking = unbreaking, Mending = mending,
                    Unbreakable = unbreakable, RepairCost = repairCost
                };
                if (!ValidStack(item))
                    throw new InvalidDataException("Invalid persisted station inventory.");
                state.Slots[slot] = item;
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
                        writer.Write(stack.Damage);
                        writer.Write((byte)stack.Unbreaking);
                        writer.Write(stack.Mending);
                        writer.Write(stack.Unbreakable);
                        writer.Write(stack.RepairCost);
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
