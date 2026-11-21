using Amethyst.Shards;

namespace Amethyst;

/// <summary>Applies consumable effects as authoritative server state and
/// keeps the client HUD synchronized. Other effect mechanics can reuse
/// this service when their event sinks are implemented.</summary>
public sealed partial class MinecraftServer
{
    private int _chorusTeleportSequence = 10000;

    /// <summary>Chorus fruit uses preloaded chunks and exact block-state
    /// identities, never a client-chosen or speculative teleport target.
    /// Failure leaves the player's location untouched.</summary>
    private async Task<bool> TryChorusTeleportAsync(
        ConnectedPlayer player, CancellationToken ct)
    {
        if (_blocks is null || _vanillaBlockStates is null ||
            player.View is not IShardChunkSubscriptions loaded)
            return false;
        PlayerPose origin = player.Pose;
        foreach (PlayerPose candidate in ChorusTeleportRules.Candidates(
            origin, _world.MinY, Random.Shared))
        {
            ct.ThrowIfCancellationRequested();
            if (!ChorusTeleportRules.TryFindLanding(_blocks,
                    _vanillaBlockStates, candidate, _world.MinY,
                    out PlayerPose destination) ||
                !_positions.Valid(destination))
                continue;
            int chunkX = (int)Math.Floor(destination.X / 16);
            int chunkZ = (int)Math.Floor(destination.Z / 16);
            if (!await loaded.HasLoadedChunkAsync(chunkX, chunkZ, ct))
                continue;
            int teleportId = Interlocked.Increment(ref _chorusTeleportSequence);
            await SpawnPackets.SendPositionAsync(player.Connection,
                teleportId, destination.X, destination.Y, destination.Z,
                destination.Yaw, destination.Pitch, ct);
            player.SetPose(destination);
            _positions.Save(player.PlayerUuid, destination);
            await player.View.UpdateAsync(player.Connection, destination, ct);
            await BroadcastMovementAsync(player, origin, destination, ct);
            return true;
        }
        return false;
    }

    private async Task ApplyConsumedEffectsAsync(ConnectedPlayer player,
        IReadOnlyList<ShardConsumeEffect> effects, CancellationToken ct)
    {
        foreach (ShardConsumeEffect effect in effects)
        {
            if (Random.Shared.NextDouble() >= effect.Chance) continue;
            if (effect.Id == "minecraft:clear_all")
            {
                foreach (string id in player.ActiveEffects.Clear())
                    if (StatusEffectProtocol.TryGetId(id, out int numeric))
                        await StatusEffectProtocol.RemoveAsync(player.Connection,
                            player.EntityId, numeric, ct);
                continue;
            }
            if (effect.Id == "minecraft:clear_poison")
            {
                if (player.ActiveEffects.Remove("minecraft:poison"))
                    await StatusEffectProtocol.RemoveAsync(player.Connection,
                        player.EntityId, 19, ct);
                continue;
            }
            if (effect.Id == "minecraft:random_teleport")
            {
                if (!await TryChorusTeleportAsync(player, ct))
                    Console.WriteLine("[items] Chorus fruit found no safe " +
                        "landing in preloaded chunks; position unchanged.");
                continue;
            }
            if (!StatusEffectProtocol.TryGetId(effect.Id, out int effectId))
            {
                Console.WriteLine($"[items] Unsupported consumed effect {effect.Id}.");
                continue;
            }
            if (effect.DurationTicks == 0)
            {
                if (effect.Id == "minecraft:instant_health")
                    player.Vitals.Heal(4 * (1 << Math.Min(8, effect.Amplifier)));
                else if (effect.Id == "minecraft:instant_damage")
                    player.Vitals.TryDamage(
                        6 * (1 << Math.Min(8, effect.Amplifier)),
                        Environment.TickCount64, out _, bypassIFrames: true);
                else if (effect.Id == "minecraft:saturation")
                    player.Vitals.TryEat(
                        Math.Min(20, 2 * (1 << Math.Min(8, effect.Amplifier))),
                        2, true);
                continue;
            }
            if (!player.ActiveEffects.Apply(effect.Id,
                effect.DurationTicks, effect.Amplifier)) continue;
            await StatusEffectProtocol.ApplyAsync(player.Connection,
                player.EntityId, effectId, effect.Amplifier,
                effect.DurationTicks, ct);
        }
    }

    internal async Task TickActiveEffectsAsync(CancellationToken ct)
    {
        foreach (ConnectedPlayer player in _players.Snapshot()
            .Where(p => p.Ready))
        {
            foreach (var effect in player.ActiveEffects.Tick())
            {
                if (effect.Expired)
                {
                    if (StatusEffectProtocol.TryGetId(effect.Id,
                        out int effectId))
                        await StatusEffectProtocol.RemoveAsync(
                            player.Connection, player.EntityId, effectId, ct);
                    continue;
                }
                if (!effect.Pulse) continue;
                switch (effect.Id)
                {
                    case "minecraft:regeneration":
                        player.Vitals.Heal(1);
                        await CombatProtocol.SendHealthAsync(player, ct);
                        break;
                    case "minecraft:poison":
                        if (player.Vitals.Snapshot().Health > 1)
                        {
                            player.Vitals.TryDamage(1, Environment.TickCount64,
                                out _, bypassIFrames: true);
                            await CombatProtocol.SendHealthAsync(player, ct);
                        }
                        break;
                    case "minecraft:hunger":
                        player.Vitals.AddExhaustion(
                            0.005f * (effect.Amplifier + 1));
                        await CombatProtocol.SendHealthAsync(player, ct);
                        break;
                }
            }
        }
    }
}
