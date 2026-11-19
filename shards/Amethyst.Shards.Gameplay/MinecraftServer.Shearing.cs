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
        if (!_livingEntities.TryShear(action.EntityId,
            out LivingEntity changed)) return false;

        if (_groundItems is not null &&
            _items.TryGetByIdentifier(
                "minecraft:" + WoolColors[changed.Color] + "_wool",
                out ShardItemDefinition wool))
            _groundItems.TrySpawn(wool.ProtocolId, Random.Shared.Next(1, 4),
                sheep.X, sheep.Y + 0.3, sheep.Z, out _);
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
