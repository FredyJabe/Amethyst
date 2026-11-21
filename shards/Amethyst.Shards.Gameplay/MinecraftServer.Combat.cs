using Amethyst.Shards;

namespace Amethyst;

/// <summary>Combat and living-entity behavior lives in Gameplay rather than
/// Amethyst's packet-only core. All damage comes from validated server actions.</summary>
public sealed partial class MinecraftServer
{
    private readonly Dictionary<Guid, Dictionary<int, LivingEntity>> _livingViews = new();

    internal LivingEntity[] LivingEntitiesForTests =>
        _livingEntities?.Snapshot() ?? [];

    internal async Task HandleAttackAsync(ConnectedPlayer attacker,
        int targetEntityId, CancellationToken ct)
    {
        if (!attacker.Ready ||
            attacker.GameMode is PlayerGameMode.Creative or PlayerGameMode.Spectator ||
            targetEntityId == attacker.EntityId ||
            targetEntityId <= 0 ||
            attacker.Vitals.Snapshot().Health <= 0)
            return;
        long now = Environment.TickCount64;
        // Do not let a client inflate its attack rate by spoofing tick packets.
        if (!attacker.Vitals.TryAttack(now)) return;
        ConnectedPlayer? victim = _players.Snapshot().FirstOrDefault(p =>
            p.Ready && p.EntityId == targetEntityId &&
            p.GameMode is PlayerGameMode.Survival or PlayerGameMode.Adventure);
        LivingEntity mob = default;
        bool sheep = victim is null &&
            _livingEntities?.TryGet(targetEntityId, out mob) == true;
        ShardEntityInstance managedTarget = default;
        bool managed = false;
        if (victim is null && !sheep && _entities is not null)
            foreach (ShardEntityInstance candidate in _entities.Snapshot())
                if (candidate.EntityId == targetEntityId && candidate.Health is > 0)
                {
                    managedTarget = candidate;
                    managed = true;
                    break;
                }
        if (victim is null && !sheep && !managed) return;
        PlayerPose source = attacker.Pose;
        PlayerPose destination = victim?.Pose ??
            (sheep ? new PlayerPose(mob.X, mob.Y, mob.Z, 0, 0, true) :
                new PlayerPose(managedTarget.X, managedTarget.Y,
                    managedTarget.Z, 0, 0, true));
        string targetKind = victim is not null ? "player" :
            EnchantmentMobCategory(sheep ? mob.Kind : managedTarget.DefinitionId);
        if (!CombatRules.InReach(source, destination)) return;
        ItemStack weapon = attacker.Inventory.Held();
        string identifier = _items is not null &&
            _items.TryGetById(weapon.ItemId, out ShardItemDefinition definition)
            ? definition.Identifier : "";
        float raw = CombatRules.MeleeDamageWithEffects(
            identifier, attacker.ActiveEffects.Snapshot());
        var attackEffects = new ShardSkillRuntime();
        attackEffects.SetNumber("damage", raw);
        attackEffects.SetNumber("knockback", 0.4);
        await EnchantmentEventAsync(weapon, "onLeftClick", ct, attackEffects,
            targetKind: targetKind, actor: attacker.PlayerUuid);
        await EnchantmentEventAsync(weapon, "onAttack", ct, attackEffects,
            targetKind: targetKind, actor: attacker.PlayerUuid);
        // Every skill has executed. Only the underlying server-side attack
        // (damage, knockback and weapon wear) is now cancelled.
        attacker.RecordAttackSwingCancellation(attackEffects.IsCancelled);
        if (attackEffects.IsCancelled) return;
        raw = (float)Math.Clamp(attackEffects.Number("damage", raw), 0, 1000);
        if (raw <= 0) return;
        double knockbackBonus = Math.Max(0, attackEffects.Number("knockback", 0.4) - 0.4);
        bool hit = false;
        if (victim is not null)
        {
            hit = await DamagePlayerAsync(victim, raw, attacker, ct,
                knockbackBonus: knockbackBonus);
        }
        else if (_livingEntities is not null &&
            _livingEntities.TryDamage(targetEntityId, raw,
                out LivingEntity updated, out bool killed))
        {
            hit = true;
            if (killed)
            {
                SpawnEntityExperience(mob.Kind, attacker, mob.X, mob.Y, mob.Z);
                // The complete, pinned 26.3 data pack is authoritative,
                // unless a user has explicitly overridden this mob in YAML.
                var lootRuntime = new ShardSkillRuntime();
                if (!mob.Sheared) lootRuntime.SetFlag("not_sheared");
                lootRuntime.SetFlag("color_" + WoolColors[mob.Color]);
                await EnchantmentEventAsync(weapon, "onEntityDrop", ct,
                    lootRuntime, targetKind: "sheep", actor: attacker.PlayerUuid);
                string lootTable = "minecraft:entities/" +
                    mob.Kind["minecraft:".Length..];
                IReadOnlyList<ShardItemDrop>? vanillaDrops =
                    _loot?.HasOverride(mob.Kind) == true ? null :
                    TryVanillaLoot(lootTable, new ShardVanillaLootContext
                    {
                        Looting = (int)Math.Clamp(
                            lootRuntime.Number("looting_level"), 0, 255),
                        FireAspect = weapon.EnchantmentLevel("fire_aspect") > 0,
                        EntityColor = WoolColors[mob.Color],
                        EntitySheared = mob.Sheared,
                        AttackerKind = "minecraft:player"
                    });
                if (vanillaDrops is not null)
                    SpawnLoot(vanillaDrops, mob.X, mob.Y + 0.25, mob.Z);
                else if (_loot?.HasDefinition(mob.Kind) == true &&
                    _skills is not null)
                {
                    await _skills.DispatchAsync(new ShardSkillEvent(
                        mob.Kind, "onDeath", mob.X, mob.Y, mob.Z,
                        EntityId: mob.EntityId, ActorUuid: attacker.PlayerUuid,
                        TargetKind: "sheep") { Runtime = lootRuntime,
                        PublisherCommitsDrops = true }, ct);
                    SpawnLoot(lootRuntime.Drops, mob.X, mob.Y + 0.25, mob.Z);
                }
                else if (_groundItems is not null && _items is not null)
                {
                    // Compatibility only when no Items loot registry was
                    // provided (e.g. legacy embedded Gameplay tests).
                    if (_items.TryGetByIdentifier("minecraft:mutton", out var meat))
                        _groundItems.TrySpawn(meat.ProtocolId, 1,
                            mob.X, mob.Y + 0.25, mob.Z, out _);
                    if (!mob.Sheared &&
                        _items.TryGetByIdentifier("minecraft:white_wool",
                            out var wool))
                        _groundItems.TrySpawn(wool.ProtocolId, 1,
                            mob.X, mob.Y + 0.25, mob.Z, out _);
                }
                foreach (ConnectedPlayer viewer in _players.Snapshot()
                    .Where(x => x.Ready))
                {
                    await LivingEntityProtocol.SendRemoveAsync(
                        viewer.Connection, mob.EntityId, ct);
                    if (_livingViews.TryGetValue(viewer.ConnectionId,
                        out var seen)) seen.Remove(mob.EntityId);
                }
            }
            else
            {
                foreach (ConnectedPlayer viewer in _players.Snapshot()
                    .Where(x => x.Ready))
                {
                    if (_livingViews.TryGetValue(viewer.ConnectionId,
                        out var seen) && seen.ContainsKey(updated.EntityId))
                    {
                        await LivingEntityProtocol.SendMetadataAsync(
                            viewer.Connection, updated, ct);
                        await CombatProtocol.SendHurtAsync(viewer.Connection,
                            updated.EntityId, ct);
                    }
                }
            }
        }
        else if (managed && _entities is not null &&
            _entities.TryDamageAndTakeDeath(targetEntityId, raw,
                out ShardEntityInstance damaged, out bool genericKilled))
        {
            hit = true;
            if (genericKilled)
                await ProcessEntityDeathAsync(damaged, attacker, weapon, ct);
            foreach (ConnectedPlayer viewer in _players.Snapshot()
                .Where(x => x.Ready && _entityViews.TryGetValue(
                    x.ConnectionId, out var seen) &&
                    seen.ContainsKey(targetEntityId)))
            {
                if (genericKilled)
                {
                    await EntityProtocol.SendRemoveAsync(
                        viewer.Connection, targetEntityId, ct);
                    _entityViews[viewer.ConnectionId].Remove(targetEntityId);
                }
                else await CombatProtocol.SendHurtAsync(
                    viewer.Connection, targetEntityId, ct);
            }
        }
        if (!hit) return;
        if (attacker.Inventory.DamageHeld(weapon.ItemId, 1))
            await InventoryProtocol.SendSlotAsync(attacker.Connection,
                attacker.Inventory, attacker.Inventory.SelectedSlot, ct);
    }

    /// <summary>Broadcast the vanilla swing only when the preceding attack
    /// did not cancel its gameplay event. A vanilla client animates its own
    /// local arm before sending punch, so there is no packet to undo that.</summary>
    internal async Task HandlePunchAsync(ConnectedPlayer player, CancellationToken ct)
    {
        if (!player.Ready || player.GameMode == PlayerGameMode.Spectator ||
            player.Vitals.Snapshot().Health <= 0) return;
        if (player.ConsumeAttackSwingCancellation()) return;
        foreach (ConnectedPlayer viewer in _players.Snapshot()
            .Where(p => p.Ready && p != player))
        {
            try
            {
                await CombatProtocol.SendSwingAsync(viewer.Connection,
                    player.EntityId, ct);
            }
            catch (IOException) { /* Disconnected peer. */ }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Damage a player from a verified physical source. Armor
    /// mitigates health damage; armor durability suffers the impact.
    /// Shields can intercept this call before health loss.</summary>
    internal async Task<bool> DamagePlayerAsync(ConnectedPlayer victim,
        float raw, ConnectedPlayer? attacker, CancellationToken ct,
        bool projectile = false, double knockbackBonus = 0,
        bool suppressThorns = false)
    {
        if (!victim.Ready || victim.GameMode is PlayerGameMode.Creative or
            PlayerGameMode.Spectator || raw <= 0) return false;
        if (attacker is not null &&
            await TryBlockShieldAsync(victim, attacker, ct))
            return true;
        PlayerPose origin = attacker?.Pose ?? victim.Pose;
        ItemStack[] gear = victim.Inventory.Snapshot().Slots;
        float armor = CombatRules.ArmorPoints(gear, _items);
        float damage = CombatRules.Mitigate(raw, armor);
        var hurt = new ShardSkillRuntime();
        hurt.SetNumber("damage", damage);
        for (int slot = 5; slot <= 8; slot++)
        {
            if (gear[slot].Count == 0) continue;
            await EnchantmentEventAsync(gear[slot], "onHurt", ct, hurt,
                targetKind: "player", damageType: projectile ? "projectile" : "generic",
                actor: victim.PlayerUuid);
        }
        // Keep executing all armor skills; cancellation prevents the vanilla
        // hit and armor wear after the entire event has been dispatched.
        if (hurt.IsCancelled) return false;
        damage = (float)Math.Clamp(hurt.Number("damage", damage), 0, 1000);
        if (damage <= 0) return false;
        // Apply Resistance and consume absorption hearts only after the
        // target's invulnerability-frame check. This is used for both melee
        // and ranged damage, and never spends absorption on rejected hits.
        if (!victim.Vitals.TryDamage(damage, Environment.TickCount64,
            out bool killed, mitigation: victim.ActiveEffects.Mitigate))
            return false;
        int durability = Math.Max(1, (int)Math.Ceiling(raw / 4));
        if (victim.Inventory.DamageArmor(durability) > 0)
            await InventoryProtocol.SendContentAsync(victim.Connection,
                victim.Inventory, ct);
        await CombatProtocol.SendHealthAsync(victim, ct);
        foreach (ConnectedPlayer observer in _players.Snapshot().Where(p =>
            p.Ready && p.ConnectionId != victim.ConnectionId))
            await CombatProtocol.SendHurtAsync(observer.Connection,
                victim.EntityId, ct);
        await CombatProtocol.SendHurtAsync(victim.Connection,
            victim.EntityId, ct);
        if (attacker is not null && !killed)
        {
            var (vx, vz) = CombatRules.Knockback(origin, victim.Pose,
                strength: 0.4 + Math.Clamp(knockbackBonus, 0, 5));
            await CombatProtocol.SendMotionAsync(victim.Connection,
                victim.EntityId, vx, 0.26, vz, ct);
        }
        if (killed)
            await CombatProtocol.SendDeathAsync(victim.Connection,
                victim.EntityId, ct);
        if (!suppressThorns && attacker is not null &&
            attacker != victim && !killed)
            foreach (ShardSkillEffect effect in hurt.Effects.Where(e => e.Id == "thorns"))
                await DamagePlayerAsync(attacker, Math.Max(1, effect.Duration),
                    victim, ct, suppressThorns: true);
        return true;
    }

    internal async Task RespawnPlayerAsync(ConnectedPlayer player, int teleportId,
        CancellationToken ct)
    {
        if (player.Vitals.Snapshot().Health > 0) return;
        player.Vitals.Respawn();
        player.ActiveEffects.Clear();
        player.SetPose(new PlayerPose(_world.SpawnXPosition,
            _world.SpawnYPosition, _world.SpawnZPosition, 0, 0, true));
        await CombatProtocol.SendRespawnAsync(player, _world, ct);
        await GameModeProtocol.SendAbilitiesAsync(
            player.Connection, player.GameMode, ct);
        await CombatProtocol.SendHealthAsync(player, ct);
        await SpawnPackets.SendPositionAsync(player.Connection, teleportId,
            _world.SpawnXPosition, _world.SpawnYPosition,
            _world.SpawnZPosition, ct);
        await player.View.UpdateAsync(player.Connection, player.Pose, ct);
    }

    internal async Task TickLivingEntitiesAsync(CancellationToken ct)
    {
        if (_livingEntities is null) return;
        LivingEntity[] entities = _livingEntities.Snapshot();
        ConnectedPlayer[] players = _players.Snapshot().Where(p => p.Ready).ToArray();
        var online = players.Select(p => p.ConnectionId).ToHashSet();
        foreach (Guid key in _livingViews.Keys.ToArray())
            if (!online.Contains(key)) _livingViews.Remove(key);

        // Only transmit mobs in the same nearby, loaded chunks as the viewer.
        foreach (ConnectedPlayer viewer in players)
        {
            if (!_livingViews.TryGetValue(viewer.ConnectionId, out var seen))
                _livingViews[viewer.ConnectionId] = seen = new();
            HashSet<int> visible = [];
            foreach (LivingEntity mob in entities)
            {
                PlayerPose pos = viewer.Pose;
                double dx = mob.X - pos.X, dz = mob.Z - pos.Z;
                if (dx * dx + dz * dz > 48 * 48 || Math.Abs(mob.Y - pos.Y) > 48)
                    continue;
                int chunkX = (int)Math.Floor(mob.X / 16),
                    chunkZ = (int)Math.Floor(mob.Z / 16);
                if (viewer.View is IShardChunkSubscriptions chunks &&
                    !await chunks.HasLoadedChunkAsync(chunkX, chunkZ, ct))
                    continue;
                visible.Add(mob.EntityId);
                if (!seen.TryGetValue(mob.EntityId, out var prior))
                    await LivingEntityProtocol.SendSpawnAsync(viewer.Connection,
                        mob, ct);
                else if (prior.Sheared != mob.Sheared ||
                    prior.Color != mob.Color || prior.Health != mob.Health)
                    await LivingEntityProtocol.SendMetadataAsync(
                        viewer.Connection, mob, ct);
                seen[mob.EntityId] = mob;
            }
            foreach (int id in seen.Keys.ToArray())
            {
                if (visible.Contains(id)) continue;
                await LivingEntityProtocol.SendRemoveAsync(
                    viewer.Connection, id, ct);
                seen.Remove(id);
            }
        }
    }

    internal bool SummonSheepAt(ConnectedPlayer player)
    {
        PlayerPose pose = player.Pose;
        return _livingEntities?.TrySpawnSheep(pose.X + 2, pose.Y,
            pose.Z + 1, out _) == true;
    }
}
