using Amethyst.Shards;

namespace Amethyst;

public sealed partial class MinecraftServer
{
    private readonly FishingWorld? _fishing;
    private readonly Dictionary<Guid, Dictionary<int, FishingBobber>> _fishingViews = new();

    internal FishingBobber[] FishingHooksForTests => _fishing?.Snapshot() ?? [];

    internal async Task<bool> HandleFishingUseAsync(ConnectedPlayer player,
        UseItemAction action, CancellationToken ct)
    {
        if (_fishing is null || _items is null ||
            player.GameMode == PlayerGameMode.Spectator || !player.Ready)
            return false;
        int slot = action.Hand == 0 ? player.Inventory.SelectedSlot : 45;
        ItemStack held = player.Inventory.Snapshot().Slots[slot];
        if (held.Count != 1 ||
            !_items.TryGetById(held.ItemId, out ShardItemDefinition item) ||
            item.Identifier != "minecraft:fishing_rod") return false;
        if (_fishing.TryReel(player.PlayerUuid, out FishingReel reel))
        {
            if (reel.Caught)
            {
                var fishingEffects = await EnchantmentEventAsync(held,
                    "onFishing", ct, actor: player.PlayerUuid);
                if (_loot?.HasDefinition("minecraft:fishing") == true &&
                    _skills is not null)
                {
                    int roll = Random.Shared.Next(100);
                    // A bounded initial Luck of the Sea bonus. Full vanilla
                    // treasure/junk tables and weighted functions are pending.
                    double luck = Math.Clamp(
                        fishingEffects.Number("fishing_luck"), 0, 3);
                    if (luck > 0 &&
                        Random.Shared.NextDouble() < luck * 0.02)
                        roll = 98 + Random.Shared.Next(2);
                    fishingEffects.SetNumber("loot_roll", roll);
                    await _skills.DispatchAsync(new ShardSkillEvent(
                        "minecraft:fishing", "onFishing", reel.Bobber.X,
                        reel.Bobber.Y, reel.Bobber.Z,
                        ActorUuid: player.PlayerUuid)
                        { Runtime = fishingEffects }, ct);
                    if (_groundItems is not null)
                        foreach (ShardItemDrop drop in fishingEffects.Drops)
                            _groundItems.TrySpawn(drop.ItemId, drop.Count,
                                reel.Bobber.X, reel.Bobber.Y + 0.4,
                                reel.Bobber.Z, out _);
                }
                else if (!fishingEffects.IsCancelled)
                {
                    // Compatibility for Gameplay tests without the Items
                    // loot registry. Live catches use the YAML skill pack.
                    var (loot, _) = FishingWorld.RollLoot();
                    if (_groundItems is not null &&
                        _items.TryGetByIdentifier(loot, out var reward))
                        _groundItems.TrySpawn(reward.ProtocolId, 1,
                            reel.Bobber.X, reel.Bobber.Y + 0.4,
                            reel.Bobber.Z, out _);
                }
                // XP is the vanilla fishing action, not an explicit
                // dropItem intent; cancelling it does not skip other skills.
                if (!fishingEffects.IsCancelled)
                    _experienceOrbs?.TrySpawn(Random.Shared.Next(1, 7),
                        reel.Bobber.X, reel.Bobber.Y + 0.4,
                        reel.Bobber.Z, out _);
            }
            foreach (ConnectedPlayer viewer in _players.Snapshot()
                .Where(x => x.Ready))
            {
                if (_fishingViews.TryGetValue(viewer.ConnectionId,
                    out var seen) && seen.Remove(reel.Bobber.EntityId))
                    await FishingProtocol.SendRemoveAsync(
                        viewer.Connection, reel.Bobber, ct);
            }
            if (player.GameMode == PlayerGameMode.Survival &&
                player.Inventory.DamageSlot(slot, held.ItemId, 1))
                await InventoryProtocol.SendSlotAsync(player.Connection,
                    player.Inventory, slot, ct);
        }
        else
        {
            var castEffects = await EnchantmentEventAsync(held,
                "onFishing", ct, actor: player.PlayerUuid);
            if (!castEffects.IsCancelled)
                _fishing.TryCast(player.PlayerUuid, player.EntityId,
                    player.Pose, out _,
                    waitReductionSeconds: (int)Math.Clamp(
                        -castEffects.Number("fishing_wait"), 0, 30));
        }
        await BlockInteractionProtocol.SendAckAsync(player.Connection,
            action.Sequence, ct);
        return true;
    }

    internal async Task TickFishingAsync(CancellationToken ct)
    {
        if (_fishing is null || _blocks is null) return;
        FishingBobber[] hooks = _fishing.Tick(_blocks);
        ConnectedPlayer[] players = _players.Snapshot()
            .Where(p => p.Ready).ToArray();
        var connected = players.Select(x => x.ConnectionId).ToHashSet();
        foreach (Guid id in _fishingViews.Keys.ToArray())
            if (!connected.Contains(id)) _fishingViews.Remove(id);

        foreach (ConnectedPlayer viewer in players)
        {
            if (!_fishingViews.TryGetValue(viewer.ConnectionId, out var seen))
                _fishingViews[viewer.ConnectionId] = seen = new();
            var visible = new HashSet<int>();
            foreach (FishingBobber hook in hooks)
            {
                PlayerPose pos = viewer.Pose;
                double dx = hook.X - pos.X, dz = hook.Z - pos.Z;
                if (dx * dx + dz * dz > 48 * 48 ||
                    Math.Abs(hook.Y - pos.Y) > 48) continue;
                int chunkX = (int)Math.Floor(hook.X / 16),
                    chunkZ = (int)Math.Floor(hook.Z / 16);
                if (viewer.View is IShardChunkSubscriptions chunks &&
                    !await chunks.HasLoadedChunkAsync(chunkX, chunkZ, ct))
                    continue;
                visible.Add(hook.EntityId);
                if (!seen.TryGetValue(hook.EntityId, out var old))
                    await FishingProtocol.SendSpawnAsync(viewer.Connection,
                        hook, ct);
                else
                {
                    if (old.X != hook.X || old.Y != hook.Y || old.Z != hook.Z)
                        await FishingProtocol.SendPositionAsync(viewer.Connection,
                            hook, ct);
                    if (old.Biting != hook.Biting)
                        await FishingProtocol.SendMetadataAsync(viewer.Connection,
                            hook, ct);
                }
                seen[hook.EntityId] = hook;
            }
            foreach (int id in seen.Keys.ToArray())
            {
                if (visible.Contains(id)) continue;
                await DroppedItemProtocol.SendRemoveAsync(
                    viewer.Connection, id, ct);
                seen.Remove(id);
            }
        }
    }
}
