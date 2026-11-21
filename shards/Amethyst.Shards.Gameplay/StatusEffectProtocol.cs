namespace Amethyst;

/// <summary>Java Edition 26.3 (protocol 777) status effect updates.
/// Numeric mob-effect IDs are static game IDs, not registries negotiated in
/// the 32 data-driven configuration registries.</summary>
public static class StatusEffectProtocol
{
    public const int UpdateMobEffect = 0x87;
    public const int RemoveMobEffect = 0x4F;

    private static readonly Dictionary<string, int> Effects =
        new(StringComparer.Ordinal)
        {
            ["minecraft:speed"] = 1,
            ["minecraft:slowness"] = 2,
            ["minecraft:haste"] = 3,
            ["minecraft:mining_fatigue"] = 4,
            ["minecraft:strength"] = 5,
            ["minecraft:instant_health"] = 6,
            ["minecraft:instant_damage"] = 7,
            ["minecraft:jump_boost"] = 8,
            ["minecraft:nausea"] = 9,
            ["minecraft:regeneration"] = 10,
            ["minecraft:resistance"] = 11,
            ["minecraft:fire_resistance"] = 12,
            ["minecraft:water_breathing"] = 13,
            ["minecraft:invisibility"] = 14,
            ["minecraft:blindness"] = 15,
            ["minecraft:night_vision"] = 16,
            ["minecraft:hunger"] = 17,
            ["minecraft:weakness"] = 18,
            ["minecraft:poison"] = 19,
            ["minecraft:wither"] = 20,
            ["minecraft:health_boost"] = 21,
            ["minecraft:absorption"] = 22,
            ["minecraft:saturation"] = 23,
            ["minecraft:glowing"] = 24,
            ["minecraft:levitation"] = 25,
            ["minecraft:luck"] = 26,
            ["minecraft:unluck"] = 27,
            ["minecraft:slow_falling"] = 28,
            ["minecraft:conduit_power"] = 29,
            ["minecraft:dolphins_grace"] = 30,
            ["minecraft:bad_omen"] = 31,
            ["minecraft:hero_of_the_village"] = 32,
            ["minecraft:darkness"] = 33,
            ["minecraft:trial_omen"] = 34,
            ["minecraft:raid_omen"] = 35,
            ["minecraft:wind_charged"] = 36,
            ["minecraft:weaving"] = 37,
            ["minecraft:oozing"] = 38,
            ["minecraft:infested"] = 39,
            ["minecraft:breath_of_the_nautilus"] = 40
        };
    public static bool TryGetId(string effect, out int id) =>
        Effects.TryGetValue(effect, out id);

    public static Task ApplyAsync(PacketChannel channel, int entityId,
        int effectId, int amplifier, int duration, CancellationToken ct) =>
        channel.SendAsync(UpdateMobEffect, writer =>
        {
            writer.WriteVarInt(entityId);
            writer.WriteVarInt(effectId);
            writer.WriteVarInt(amplifier);
            writer.WriteVarInt(duration);
            writer.WriteByte(0x06); // particles and effect icon
        }, ct);

    public static Task RemoveAsync(PacketChannel channel, int entityId,
        int effectId, CancellationToken ct) =>
        channel.SendAsync(RemoveMobEffect, writer =>
        {
            writer.WriteVarInt(entityId);
            writer.WriteVarInt(effectId);
        }, ct);
}
