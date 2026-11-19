using Amethyst.Shards;

namespace Amethyst;

public sealed partial class MinecraftServer
{
    private readonly Dictionary<Guid, Dictionary<int, ProjectileEntity>>
        _projectileViews = new();

    internal ProjectileEntity[] ProjectilesForTests =>
        _projectiles?.Snapshot() ?? [];

    private int ItemId(string identifier)
    {
        return _items is not null &&
            _items.TryGetByIdentifier(identifier, out ShardItemDefinition item)
            ? item.ProtocolId : 0;
    }

    internal async Task<bool> HandleProjectileUseAsync(
        ConnectedPlayer player, UseItemAction action, CancellationToken ct)
    {
        if (_projectiles is null || _items is null ||
            player.GameMode == PlayerGameMode.Spectator)
            return false;
        int slot = action.Hand == 0 ? player.Inventory.SelectedSlot : 45;
        ItemStack stack = player.Inventory.Snapshot().Slots[slot];
        if (stack.Count <= 0 ||
            !_items.TryGetById(stack.ItemId, out var definition))
            return false;
        string name = definition.Identifier;
        bool charge = name is "minecraft:bow" or "minecraft:crossbow"
            or "minecraft:trident";
        if (charge)
        {
            player.UseState.BeginCharge(action.Hand, stack.ItemId,
                Environment.TickCount64);
            await BlockInteractionProtocol.SendAckAsync(player.Connection,
                action.Sequence, ct);
            return true;
        }
        ProjectileKind kind = name switch
        {
            "minecraft:snowball" => ProjectileKind.Snowball,
            "minecraft:egg" => ProjectileKind.Egg,
            "minecraft:ender_pearl" => ProjectileKind.EnderPearl,
            "minecraft:splash_potion" => ProjectileKind.SplashPotion,
            _ => (ProjectileKind)255
        };
        if (!Enum.IsDefined(kind)) return false;
        bool creative = player.GameMode == PlayerGameMode.Creative;
        if (!creative && !player.Inventory.ConsumeSlot(slot, stack.ItemId))
        {
            await BlockInteractionProtocol.SendAckAsync(player.Connection,
                action.Sequence, ct);
            return true;
        }
        if (_projectiles.TryLaunch(player.PlayerUuid, kind, player.Pose,
            1.5, kind == ProjectileKind.EnderPearl ? 0 : 0,
            ItemStack.Empty, out _))
        {
            if (!creative)
                await InventoryProtocol.SendSlotAsync(player.Connection,
                    player.Inventory, slot, ct);
        }
        else if (!creative)
            player.Inventory.GiveStack(new ItemStack(stack.ItemId, 1));
        await BlockInteractionProtocol.SendAckAsync(player.Connection,
            action.Sequence, ct);
        return true;
    }

    /// <summary>Called only on the actual Release Use Item packet.
    /// Changing a hotbar slot cancels, rather than firing, a charge.</summary>
    internal async Task ReleaseProjectileChargeAsync(ConnectedPlayer player,
        CancellationToken ct)
    {
        var charge = player.UseState.StopCharge();
        if (charge is not { } fired || _items is null ||
            _projectiles is null || !player.Ready ||
            player.GameMode == PlayerGameMode.Spectator)
            return;
        int slot = fired.Hand == 0 ? player.Inventory.SelectedSlot : 45;
        ItemStack stack = player.Inventory.Snapshot().Slots[slot];
        if (stack.Count != 1 || stack.ItemId != fired.ItemId ||
            !_items.TryGetById(stack.ItemId, out var item)) return;
        string name = item.Identifier;
        bool creative = player.GameMode == PlayerGameMode.Creative;
        long heldMs = Math.Max(0, Environment.TickCount64 - fired.Started);
        ProjectileKind kind;
        double speed;
        float damage;
        ItemStack recoverable = ItemStack.Empty;
        int ammoId = 0;
        if (name is "minecraft:bow" or "minecraft:crossbow")
        {
            // Vanilla-style bow pull curve; crossbows use their longer
            // loading period (minimum 1.0s for a full shot).
            double seconds = heldMs / 1000d;
            if (name == "minecraft:crossbow" && seconds < 0.9)
                return;
            double power = name == "minecraft:bow" ?
                Math.Min(1, (seconds * seconds + 2 * seconds) / 3) : 1;
            if (power < 0.1) return;
            int arrow = ItemId("minecraft:arrow");
            int spectral = ItemId("minecraft:spectral_arrow");
            ammoId = player.Inventory.HasItem(arrow) ? arrow :
                player.Inventory.HasItem(spectral) ? spectral : 0;
            if (ammoId == 0 && !creative) return;
            kind = ammoId == spectral ? ProjectileKind.SpectralArrow :
                ProjectileKind.Arrow;
            speed = 3 * power;
            damage = (float)(2 + power * 4);
            if (ammoId > 0) recoverable = new ItemStack(ammoId, 1);
        }
        else if (name == "minecraft:trident")
        {
            if (heldMs < 500) return;
            kind = ProjectileKind.Trident;
            speed = 2.5;
            damage = 8;
            recoverable = DurabilityRules.ApplyWear(stack,
                item.MaxDurability, 1);
        }
        else return;
        if (_projectiles.Snapshot().Length >= ProjectileWorld.MaximumProjectiles)
            return;
        if (!creative)
        {
            if (ammoId > 0 && !player.Inventory.TryConsumeItem(ammoId, 1))
                return;
            if (kind == ProjectileKind.Trident &&
                !player.Inventory.ConsumeSlot(slot, stack.ItemId))
                return;
        }
        if (!_projectiles.TryLaunch(player.PlayerUuid, kind, player.Pose,
            speed, damage, creative ? ItemStack.Empty : recoverable,
            out _))
            return;
        if (kind != ProjectileKind.Trident && !creative &&
            player.Inventory.DamageSlot(slot, stack.ItemId, 1))
            await InventoryProtocol.SendSlotAsync(player.Connection,
                player.Inventory, slot, ct);
        else if (kind == ProjectileKind.Trident && !creative)
            await InventoryProtocol.SendSlotAsync(player.Connection,
                player.Inventory, slot, ct);
        // Arrow ammo may come from any slot, so resynchronize the inventory.
        if (ammoId > 0 && !creative)
            await InventoryProtocol.SendContentAsync(player.Connection,
                player.Inventory, ct);
    }

    internal async Task TickProjectilesAsync(CancellationToken ct)
    {
        if (_projectiles is null || _blocks is null) return;
        ConnectedPlayer[] players = _players.Snapshot()
            .Where(p => p.Ready).ToArray();
        ProjectileFrame frame = _projectiles.Tick(_blocks, players,
            _livingEntities?.Snapshot() ?? []);
        ProjectileEntity[] present = frame.Moving;
        var online = players.Select(p => p.ConnectionId).ToHashSet();
        foreach (Guid id in _projectileViews.Keys.ToArray())
            if (!online.Contains(id)) _projectileViews.Remove(id);
        foreach (ConnectedPlayer viewer in players)
        {
            if (!_projectileViews.TryGetValue(viewer.ConnectionId, out var seen))
                _projectileViews[viewer.ConnectionId] = seen = new();
            var visible = new HashSet<int>();
            foreach (ProjectileEntity projectile in present)
            {
                PlayerPose pose = viewer.Pose;
                double dx = projectile.X - pose.X, dz = projectile.Z - pose.Z;
                if (dx * dx + dz * dz > 48 * 48 ||
                    Math.Abs(projectile.Y - pose.Y) > 48) continue;
                int cx = (int)Math.Floor(projectile.X / 16),
                    cz = (int)Math.Floor(projectile.Z / 16);
                if (viewer.View is IShardChunkSubscriptions subscriber &&
                    !await subscriber.HasLoadedChunkAsync(cx, cz, ct))
                    continue;
                visible.Add(projectile.EntityId);
                if (!seen.ContainsKey(projectile.EntityId))
                    await ProjectileProtocol.SendSpawnAsync(viewer.Connection,
                        projectile, ct);
                else await ProjectileProtocol.SendPositionAsync(viewer.Connection,
                    projectile, ct);
                seen[projectile.EntityId] = projectile;
            }
            foreach (int id in seen.Keys.ToArray())
            {
                if (visible.Contains(id)) continue;
                await DroppedItemProtocol.SendRemoveAsync(
                    viewer.Connection, id, ct);
                seen.Remove(id);
            }
        }
        foreach (ProjectileImpact impact in frame.Impacts)
        {
            ProjectileEntity p = impact.Projectile;
            ConnectedPlayer? attacker = players.FirstOrDefault(x =>
                x.PlayerUuid == p.Owner);
            if (impact.PlayerHit != Guid.Empty)
            {
                ConnectedPlayer? victim = players.FirstOrDefault(x =>
                    x.PlayerUuid == impact.PlayerHit);
                if (victim is not null && p.Damage > 0)
                    await DamagePlayerAsync(victim, p.Damage, attacker, ct,
                        projectile: true);
            }
            if (impact.MobHit != 0 && _livingEntities is not null &&
                p.Damage > 0 &&
                _livingEntities.TryDamage(impact.MobHit, p.Damage,
                    out var updated, out bool killed) && killed &&
                _groundItems is not null && _items is not null &&
                _items.TryGetByIdentifier("minecraft:mutton", out var mutton))
                _groundItems.TrySpawn(mutton.ProtocolId, 1,
                    updated.X, updated.Y, updated.Z, out _);
            if (p.CanRecover && _groundItems is not null)
                _groundItems.TrySpawn(p.Recoverable, p.X, p.Y,
                    p.Z, out _);
            // Ender pearls teleport the original thrower; the server alone
            // decides the collision position, never a client-reported hit.
            if (p.Kind == ProjectileKind.EnderPearl && attacker is not null)
            {
                attacker.SetPose(new PlayerPose(p.X, p.Y + 0.1, p.Z,
                    attacker.Pose.Yaw, attacker.Pose.Pitch, false));
                await SpawnPackets.SendPositionAsync(attacker.Connection,
                    SpawnPackets.TeleportId, p.X, p.Y + 0.1, p.Z, ct);
                await DamagePlayerAsync(attacker, 5, null, ct);
            }
            foreach (ConnectedPlayer viewer in players)
            {
                if (_projectileViews.TryGetValue(viewer.ConnectionId,
                    out var seen) && seen.Remove(p.EntityId))
                    await ProjectileProtocol.SendRemoveAsync(viewer.Connection,
                        p, ct);
            }
        }
    }
}
