using Amethyst.Shards;

namespace Amethyst;

/// <summary>Applies consumable effects as authoritative server state and
/// keeps the client HUD synchronized. Other effect mechanics can reuse
/// this service when their event sinks are implemented.</summary>
public sealed partial class MinecraftServer
{
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
                // A separate safe teleport scheduler must validate collision,
                // loaded chunks and world border. Never teleport through solids.
                Console.WriteLine("[items] Random teleport requested; " +
                    "safe teleport targeting is not yet implemented.");
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
