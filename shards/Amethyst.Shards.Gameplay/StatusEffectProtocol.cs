namespace Amethyst;

/// <summary>Java Edition 26.3 (protocol 777) status effect updates.
/// Numeric mob-effect IDs are static game IDs, not registries negotiated in
/// the 32 data-driven configuration registries.</summary>
public static class StatusEffectProtocol
{
    public const int UpdateMobEffect = 0x87;
    public const int RemoveMobEffect = 0x4F;

    // Vanilla 26.3's actual builtin mob_effect registry is zero-based.
    // Never reuse pre-flattening effect IDs; they are one-based and differ.
    public static bool TryGetId(string effect, out int id) =>
        VanillaBuiltinIds.TryGet("minecraft:mob_effect", effect, out id);

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
