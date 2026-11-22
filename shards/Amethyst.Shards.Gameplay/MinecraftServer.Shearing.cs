using Amethyst.Shards;

namespace Amethyst;

public sealed partial class MinecraftServer
{
    private static readonly string[] WoolColors =
    [
        "white", "orange", "magenta", "light_blue",
        "yellow", "lime", "pink", "gray", "light_gray",
        "cyan", "purple", "blue", "brown", "green", "red", "black"
    ];

    /// <summary>One validated entity right-click publisher for both native
    /// sheep and generic entities. Item/enchantment Skills execute before any
    /// milk/shear mutation. A cancellation suppresses the default interaction,
    /// but does not interrupt remaining skill instructions.</summary>
    internal async Task<bool> HandleValidatedEntityInteractionAsync(
        ConnectedPlayer player, EntityInteraction action, CancellationToken ct)
    {
        if (!player.Ready || player.GameMode == PlayerGameMode.Spectator ||
            action.Hand is < 0 or > 1 || _entities is null)
            return false;
        ShardEntityInstance target = default;
        foreach (ShardEntityInstance candidate in _entities.Snapshot())
            if (candidate.EntityId == action.EntityId)
            {
                target = candidate;
                break;
            }
        if (target.EntityId <= 0) return false;
        PlayerPose position = player.Pose;
        double dx = target.X - position.X, dz = target.Z - position.Z;
        double dy = target.Y + 0.8 - (position.Y + 1.62);
        if (dx * dx + dy * dy + dz * dz > 25) return false;
        ItemStack[] slots = player.Inventory.Snapshot().Slots;
        ItemStack held = slots[action.Hand == 0
            ? player.Inventory.SelectedSlot : 45];
        var runtime = await EnchantmentEventAsync(held, "onRightClick", ct,
            actor: player.PlayerUuid);
        if (runtime.IsCancelled) return true;
        bool handled = await HandleEntityInteractAsync(player, action, ct);
        if (!handled)
            await HandleEntityInteractionAsync(player, action, ct);
        return true;
    }

    /// <summary>Interact with a real, server-known sheep. A client may only
    /// send an entity ID and hand; the server checks distance, gamemode,
    /// inventory and whether the sheep actually has wool.</summary>
    internal async Task<bool> HandleEntityInteractAsync(ConnectedPlayer player,
        EntityInteraction action, CancellationToken ct)
    {
        if (!player.Ready || player.GameMode == PlayerGameMode.Spectator ||
            _livingEntities is null || _items is null ||
            !_livingEntities.TryGet(action.EntityId, out LivingEntity sheep))
            return false;
        PlayerPose pose = player.Pose;
        double dx = pose.X - sheep.X, dz = pose.Z - sheep.Z,
            dy = pose.Y + 1 - sheep.Y - 0.8;
        if (dx * dx + dy * dy + dz * dz > 3.5 * 3.5) return false;

        int slot = action.Hand == 0 ? player.Inventory.SelectedSlot : 45;
        ItemStack held = player.Inventory.Snapshot().Slots[slot];
        if (held.Count != 1 || !_items.TryGetById(
            held.ItemId, out ShardItemDefinition item) ||
            item.Identifier != "minecraft:shears")
            return false;
        if (sheep.Sheared) return false;
        // Run every onShear instruction BEFORE changing authoritative state.
        // cancelEvent suppresses sheep shearing and tool wear, not later skill
        // instructions or their explicitly requested physical drop intents.
        var effects = new ShardSkillRuntime();
        effects.SetFlag("color_" + WoolColors[sheep.Color]);
        if (_skills is not null)
            await _skills.DispatchAsync(new ShardSkillEvent(
                "sheep", "onShear", sheep.X, sheep.Y, sheep.Z,
                EntityId: sheep.EntityId, FallbackOwnerId: sheep.Kind,
                ActorUuid: player.PlayerUuid, ItemId: held.ItemId,
                Count: held.Count, TargetKind: "sheep")
                { Runtime = effects, PublisherCommitsDrops = true }, ct);
        if (effects.IsCancelled)
        {
            SpawnLoot(effects.Drops, sheep.X, sheep.Y + 0.3, sheep.Z);
            return true;
        }
        if (!_livingEntities.TryShear(action.EntityId,
            out LivingEntity changed)) return false;
        // OnShear is a separate explicit interaction skill. No configured
        // dropItem means no wool drop: neither Entities nor Gameplay owns a
        // default sheep reward, even when the interaction itself succeeds.
        SpawnLoot(effects.Drops, sheep.X, sheep.Y + 0.3, sheep.Z);
        foreach (ConnectedPlayer observer in _players.Snapshot()
            .Where(x => x.Ready))
        {
            if (_livingViews.TryGetValue(observer.ConnectionId,
                out var seen) && seen.ContainsKey(sheep.EntityId))
            {
                await LivingEntityProtocol.SendMetadataAsync(
                    observer.Connection, changed, ct);
                seen[sheep.EntityId] = changed;
            }
        }
        if (player.GameMode != PlayerGameMode.Creative &&
            player.Inventory.DamageSlot(slot, held.ItemId, 1))
            await InventoryProtocol.SendSlotAsync(player.Connection,
                player.Inventory, slot, ct);
        return true;
    }

    private async Task TickWoolRegrowthAsync(CancellationToken ct)
    {
        if (_livingEntities is null) return;
        // Grazing simulation: sheared sheep regrow after a configurable
        // interval, with state persisted across server restarts.
        LivingEntity[] regrown = _livingEntities.Regrow(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (regrown.Length == 0) return;
        foreach (ConnectedPlayer observer in _players.Snapshot()
            .Where(x => x.Ready))
        {
            if (!_livingViews.TryGetValue(observer.ConnectionId,
                out var seen)) continue;
            foreach (LivingEntity sheep in regrown)
            {
                if (!seen.ContainsKey(sheep.EntityId)) continue;
                await LivingEntityProtocol.SendMetadataAsync(
                    observer.Connection, sheep, ct);
                seen[sheep.EntityId] = sheep;
            }
        }
    }
}
