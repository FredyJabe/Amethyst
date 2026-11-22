using Amethyst.Shards;

namespace Amethyst;

public sealed partial class MinecraftServer
{
    private readonly Dictionary<Guid, Dictionary<int, ProjectileEntity>>
        _projectileViews = new();
    private readonly IShardProjectileWorld? _projectiles;

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
        // Preserve authoritative potion_contents and custom effects for
        // splash impacts. The projectile carries a copy of one stack, not
        // the client-provided inventory or an item eligible for pickup.
        ItemStack payload = kind == ProjectileKind.SplashPotion
            ? stack with { Count = 1 } : ItemStack.Empty;
        if (_projectiles.TryLaunch(player.PlayerUuid, kind, player.Pose,
            1.5, 0, ItemStack.Empty, out _, payload))
        {
            if (!creative)
                await InventoryProtocol.SendSlotAsync(player.Connection,
                    player.Inventory, slot, ct);
        }
        else if (!creative)
            player.Inventory.GiveStack(stack with { Count = 1 });
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
            recoverable = creative ? stack : DurabilityRules.ApplyWear(stack,
                item.MaxDurability, 1,
                preventsWear: player.Inventory.PreventsDurabilityWear);
        }
        else return;
        var projectileEffects = new ShardSkillRuntime();
        projectileEffects.SetNumber("projectile_damage", damage);
        projectileEffects.SetNumber("projectile_count", 1);
        projectileEffects.SetNumber("piercing", 0);
        await EnchantmentEventAsync(stack, "onProjectileLaunch", ct,
            projectileEffects, actor: player.PlayerUuid);
        // All configured launch skills have executed; cancellation prevents
        // the vanilla launch, ammo consumption, and durability wear.
        if (projectileEffects.IsCancelled) return;
        damage = (float)Math.Clamp(projectileEffects.Number(
            "projectile_damage", damage), 0, 1000);
        if (_projectiles.Snapshot().Length >= 1024)
            return;
        if (!creative)
        {
            if (ammoId > 0 && !projectileEffects.HasFlag("infinity") &&
                !player.Inventory.TryConsumeItem(ammoId, 1))
                return;
            if (kind == ProjectileKind.Trident &&
                !player.Inventory.ConsumeSlot(slot, stack.ItemId))
                return;
        }
        if (!_projectiles.TryLaunch(player.PlayerUuid, kind, player.Pose,
            speed, damage, creative ? ItemStack.Empty : recoverable,
            out _, sourceWeapon: stack))
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

    /// <summary>Commit splash effects to server-owned player state at the
    /// projectile's validated impact position. Distance attenuates duration
    /// and instant healing/damage. No client-reported potion effects are used.</summary>
    internal async Task ApplySplashPotionAsync(ProjectileEntity potion,
        Guid directHit, IReadOnlyList<ConnectedPlayer> players,
        CancellationToken ct)
    {
        if (potion.Kind != ProjectileKind.SplashPotion || potion.Payload.Count < 1)
            return;
        IReadOnlyList<ShardConsumeEffect> effects;
        try
        {
            effects = ConsumableEffects.ForStack(potion.Payload, []);
        }
        catch (Exception ex) when (ex is InvalidDataException or
            NotSupportedException or System.Text.Json.JsonException)
        {
            Console.WriteLine("[items] Rejected invalid splash potion: " +
                ex.Message);
            return;
        }
        foreach (ConnectedPlayer player in players)
        {
            if (!player.Ready || player.GameMode == PlayerGameMode.Spectator ||
                player.Vitals.Snapshot().Health <= 0) continue;
            PlayerPose pos = player.Pose;
            double dx = potion.X - pos.X, dz = potion.Z - pos.Z;
            double dy = potion.Y - (pos.Y + 0.9);
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > 4) continue;
            double intensity = directHit == player.PlayerUuid ? 1 :
                Math.Clamp(1 - distance / 4, 0, 1);
            if (intensity <= 0) continue;
            ShardConsumeEffect[] scaled = effects
                .Select(e => e.DurationTicks > 0
                    ? e with { DurationTicks =
                        (int)Math.Floor(e.DurationTicks * intensity) }
                    : e)
                .Where(e => e.DurationTicks == 0 ||
                    e.DurationTicks >= 20)
                .ToArray();
            await ApplyConsumedEffectsAsync(player, scaled, ct, intensity);
            await CombatProtocol.SendHealthAsync(player, ct);
        }
    }

    internal async Task TickProjectilesAsync(CancellationToken ct)
    {
        if (_projectiles is null || _blocks is null) return;
        ConnectedPlayer[] players = _players.Snapshot()
            .Where(p => p.Ready).ToArray();
        ProjectileFrame frame = _projectiles.Tick(_blocks,
            players.Where(p => p.Ready && p.GameMode != PlayerGameMode.Spectator)
                .Select(p => new ProjectilePlayer(p.PlayerUuid, p.Pose)).ToArray(),
            _entities?.Snapshot() ?? []);
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
            var hitSkills = new ShardSkillRuntime();
            hitSkills.SetNumber("damage", p.Damage);
            string? targetKind = impact.PlayerHit != Guid.Empty ? "player" : null;
            if (impact.MobHit != 0 && _entities is not null)
                targetKind = EnchantmentMobCategory(_entities.Snapshot()
                    .FirstOrDefault(e => e.EntityId == impact.MobHit).DefinitionId);
            if (_vanillaBlockStates is not null && _blocks is not null &&
                _vanillaBlockStates.TryGet(_blocks.GetBlockState(
                    (int)Math.Floor(p.X), (int)Math.Floor(p.Y),
                    (int)Math.Floor(p.Z)), out string fluid, out _) &&
                fluid == "minecraft:water")
                hitSkills.SetFlag("wet_target");
            await EnchantmentEventAsync(p.SourceWeapon, "onProjectileHit", ct,
                hitSkills, targetKind: targetKind, actor: attacker?.PlayerUuid);
            float impactDamage = (float)Math.Clamp(
                hitSkills.Number("damage", p.Damage), 0, 1000);
            bool cancelDamage = hitSkills.IsCancelled;
            if (impact.PlayerHit != Guid.Empty)
            {
                ConnectedPlayer? victim = players.FirstOrDefault(x =>
                    x.PlayerUuid == impact.PlayerHit);
                if (victim is not null && !cancelDamage && impactDamage > 0)
                    await DamagePlayerAsync(victim, impactDamage, attacker, ct,
                        projectile: true);
            }
            if (p.Kind == ProjectileKind.SplashPotion && p.Payload.Count > 0)
                await ApplySplashPotionAsync(p, impact.PlayerHit, players, ct);
            // Both livestock and generic YAML mobs are owned by Entities.
            // Commit the one-shot death through the same loot pipeline as
            // melee, never a hard-coded sheep reward or duplicate tick drop.
            if (impact.MobHit != 0 && _entities is not null &&
                !cancelDamage && impactDamage > 0 &&
                _entities.TryDamageAndTakeDeath(impact.MobHit, impactDamage,
                    out ShardEntityInstance injured, out bool killedMob))
            {
                if (killedMob)
                    await ProcessEntityDeathAsync(injured, attacker,
                        p.SourceWeapon, ct,
                        damageType: p.Kind switch
                        {
                            ProjectileKind.Trident => "minecraft:trident",
                            ProjectileKind.Arrow or ProjectileKind.SpectralArrow =>
                                "minecraft:arrow",
                            _ => null
                        },
                        directDamagerKind: p.Kind switch
                        {
                            ProjectileKind.Trident => "minecraft:trident",
                            ProjectileKind.Arrow or ProjectileKind.SpectralArrow =>
                                "minecraft:arrow",
                            _ => null
                        });
                else
                    foreach (ConnectedPlayer viewer in players.Where(v =>
                        _entityViews.TryGetValue(v.ConnectionId, out var seen) &&
                        seen.ContainsKey(impact.MobHit)))
                        await CombatProtocol.SendHurtAsync(
                            viewer.Connection, impact.MobHit, ct);
            }
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
