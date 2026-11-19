namespace Amethyst;

/// <summary>Right-click shield activation and server-side directional
/// blocking. Later use-item mechanics reuse this packet handler.</summary>
public sealed partial class MinecraftServer
{
    internal async Task HandleUseItemAsync(ConnectedPlayer player,
        UseItemAction action, CancellationToken ct)
    {
        if (!player.Ready) return;
        if (await HandleShieldUseAsync(player, action, ct)) return;
        if (await HandleProjectileUseAsync(player, action, ct)) return;
        if (await HandleFishingUseAsync(player, action, ct)) return;
        if (await HandleElytraUseAsync(player, action, ct)) return;
        await BlockInteractionProtocol.SendAckAsync(player.Connection,
            action.Sequence, ct);
    }

    private async Task<bool> HandleShieldUseAsync(ConnectedPlayer player,
        UseItemAction action, CancellationToken ct)
    {
        if (player.GameMode == PlayerGameMode.Spectator) return false;
        ItemStack stack = action.Hand == 0 ? player.Inventory.Held() :
            player.Inventory.Snapshot().Slots[45];
        if (!ShieldRules.IsShield(stack, _items)) return false;
        bool started = player.UseState.BeginShield(action.Hand,
            Environment.TickCount64);
        await BlockInteractionProtocol.SendAckAsync(player.Connection,
            action.Sequence, ct);
        if (started) await SyncShieldMetadataAsync(player, ct);
        return true;
    }

    internal async Task ReleaseUsingItemAsync(ConnectedPlayer player,
        CancellationToken ct)
    {
        await ReleaseProjectileChargeAsync(player, ct);
        if (player.UseState.ShieldHand >= 0)
        {
            player.UseState.StopShield();
            await SyncShieldMetadataAsync(player, ct);
        }
    }

    private async Task SyncShieldMetadataAsync(ConnectedPlayer player,
        CancellationToken ct)
    {
        int hand = player.UseState.ShieldHand;
        foreach (ConnectedPlayer observer in _players.Snapshot()
            .Where(p => p.Ready))
            await UseItemProtocol.SendShieldMetadataAsync(
                observer.Connection, player.EntityId, hand, ct);
    }

    /// <returns>True if the hit was fully blocked, without health loss.
    /// Axe attackers disable a shield, and successful blocks use durability.</returns>
    internal async Task<bool> TryBlockShieldAsync(ConnectedPlayer victim,
        ConnectedPlayer attacker, CancellationToken ct)
    {
        if (!victim.UseState.IsBlocking(Environment.TickCount64) ||
            !ShieldRules.FacesAttacker(victim.Pose, attacker.Pose))
            return false;
        int hand = victim.UseState.ShieldHand;
        int slot = hand == 0 ? victim.Inventory.SelectedSlot : 45;
        if (hand < 0) return false;
        ItemStack shield = victim.Inventory.Snapshot().Slots[slot];
        if (!ShieldRules.IsShield(shield, _items))
        {
            await ReleaseUsingItemAsync(victim, ct);
            return false;
        }
        bool changed = victim.GameMode != PlayerGameMode.Creative &&
            victim.Inventory.DamageSlot(slot, shield.ItemId, 1);
        if (changed)
            await InventoryProtocol.SendSlotAsync(victim.Connection,
                victim.Inventory, slot, ct);
        ItemStack after = victim.Inventory.Snapshot().Slots[slot];
        if (after.ItemId != shield.ItemId || after.Count == 0)
        {
            await ReleaseUsingItemAsync(victim, ct);
        }
        else if (ShieldRules.IsAxe(attacker.Inventory.Held(), _items))
        {
            victim.UseState.DisableShield(Environment.TickCount64);
            await SyncShieldMetadataAsync(victim, ct);
        }
        return true;
    }
}
