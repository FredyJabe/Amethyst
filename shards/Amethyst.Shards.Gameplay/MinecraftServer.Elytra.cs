using Amethyst.Shards;

namespace Amethyst;

public sealed partial class MinecraftServer
{
    internal async Task HandleFlightCommandAsync(ConnectedPlayer player,
        PlayerCommandAction command, CancellationToken ct)
    {
        if (command.EntityId != player.EntityId ||
            command.Action != FlightProtocol.StartFallFlying ||
            !player.Ready || player.GameMode == PlayerGameMode.Spectator ||
            player.Vitals.Snapshot().Health <= 0 ||
            _items is null || player.Pose.OnGround) return;
        ItemStack elytra = player.Inventory.Snapshot().Slots[6];
        if (!_items.TryGetById(elytra.ItemId,
            out ShardItemDefinition definition) ||
            definition.Identifier != "minecraft:elytra" ||
            !FlightRules.CanStart(player.Pose.OnGround, elytra,
                definition.MaxDurability) ||
            !player.Flight.Begin(Environment.TickCount64))
            return;
        await SyncElytraStateAsync(player, ct);
    }

    private async Task SyncElytraStateAsync(ConnectedPlayer player,
        CancellationToken ct)
    {
        bool active = player.Flight.IsGliding;
        ItemStack equipped = player.Inventory.Snapshot().Slots[6];
        foreach (ConnectedPlayer viewer in _players.Snapshot()
            .Where(x => x.Ready))
        {
            if (equipped.Count > 0)
                await FlightProtocol.SendElytraEquipmentAsync(
                    viewer.Connection, player.EntityId, equipped, ct);
            await FlightProtocol.SendGlidingAsync(viewer.Connection,
                player.EntityId, active, ct);
        }
    }

    internal async Task<bool> HandleElytraUseAsync(ConnectedPlayer player,
        UseItemAction action, CancellationToken ct)
    {
        if (!player.Ready || !player.Flight.IsGliding || _items is null ||
            player.GameMode == PlayerGameMode.Spectator) return false;
        int slot = action.Hand == 0 ? player.Inventory.SelectedSlot : 45;
        ItemStack held = player.Inventory.Snapshot().Slots[slot];
        if (held.Count <= 0 ||
            !_items.TryGetById(held.ItemId, out ShardItemDefinition item) ||
            item.Identifier != "minecraft:firework_rocket")
            return false;
        bool creative = player.GameMode == PlayerGameMode.Creative;
        if (!creative && !player.Inventory.ConsumeSlot(slot, held.ItemId))
            return true;
        if (!player.Flight.Boost(Environment.TickCount64)) return true;
        var (vx, vy, vz) = FlightRules.RocketImpulse(
            player.Pose.Yaw, player.Pose.Pitch);
        await CombatProtocol.SendMotionAsync(player.Connection,
            player.EntityId, vx, vy, vz, ct);
        if (!creative)
            await InventoryProtocol.SendSlotAsync(player.Connection,
                player.Inventory, slot, ct);
        await BlockInteractionProtocol.SendAckAsync(player.Connection,
            action.Sequence, ct);
        return true;
    }

    internal async Task UpdateGlideMovementAsync(ConnectedPlayer player,
        PlayerPose previous, PlayerPose current, byte flags,
        CancellationToken ct)
    {
        if (!player.Flight.IsGliding) return;
        if (current.OnGround || player.GameMode == PlayerGameMode.Spectator)
        {
            if (player.Flight.End()) await SyncElytraStateAsync(player, ct);
            return;
        }
        // Elytra wall collisions inflict kinetic damage. The client reports
        // horizontal collision, but the magnitude is computed from verified
        // server-accepted movement rather than from a client damage value.
        if ((flags & 2) != 0)
        {
            double dx = current.X - previous.X,
                dz = current.Z - previous.Z;
            double speed = Math.Sqrt(dx * dx + dz * dz);
            if (speed > 0.5)
            {
                await DamagePlayerAsync(player, (float)Math.Clamp(
                    (speed - 0.5) * 5, 1, 20), null, ct);
                if (player.Flight.End())
                    await SyncElytraStateAsync(player, ct);
            }
        }
    }

    /// <summary>One 20Hz equipment check. Elytra lose one durability per
    /// second of flight (subject to Unbreaking) and stop at one remaining
    /// usable point instead of disappearing like a broken ordinary tool.</summary>
    internal async Task TickElytraAsync(CancellationToken ct,
        long? nowMilliseconds = null)
    {
        if (_items is null) return;
        long now = nowMilliseconds ?? Environment.TickCount64;
        foreach (ConnectedPlayer player in _players.Snapshot()
            .Where(p => p.Ready && p.Flight.IsGliding))
        {
            ItemStack chest = player.Inventory.Snapshot().Slots[6];
            if (player.Pose.OnGround ||
                !_items.TryGetById(chest.ItemId, out var definition) ||
                definition.Identifier != "minecraft:elytra" ||
                !FlightRules.CanStart(false, chest, definition.MaxDurability))
            {
                if (player.Flight.End())
                    await SyncElytraStateAsync(player, ct);
                continue;
            }
            if (!player.Flight.WearDue(now) ||
                player.GameMode == PlayerGameMode.Creative) continue;
            bool worn = player.Inventory.DamageSlot(6, chest.ItemId, 1);
            if (worn)
                await InventoryProtocol.SendSlotAsync(player.Connection,
                    player.Inventory, 6, ct);
            ItemStack after = player.Inventory.Snapshot().Slots[6];
            if (!FlightRules.CanStart(false, after, definition.MaxDurability))
            {
                if (player.Flight.End())
                    await SyncElytraStateAsync(player, ct);
            }
        }
    }
}
