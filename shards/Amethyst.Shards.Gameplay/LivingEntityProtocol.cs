namespace Amethyst;

/// <summary>Java 26.3 sheep spawn and metadata. Wool metadata index 18
/// packs color bits 0..3 and sheared bit 4.</summary>
public static class LivingEntityProtocol
{
    public const int SheepEntityType = 114;
    public const int ClientboundSetEntityData = 0x65;
    public const int ClientboundAddEntity = 0x01;
    public const int ClientboundRemoveEntities = 0x4E;

    public static async Task SendSpawnAsync(PacketChannel channel,
        LivingEntity sheep, CancellationToken ct)
    {
        await channel.SendAsync(ClientboundAddEntity, p =>
        {
            p.WriteVarInt(sheep.EntityId); p.WriteUuid(sheep.Uuid);
            p.WriteVarInt(SheepEntityType);
            p.WriteDouble(sheep.X); p.WriteDouble(sheep.Y); p.WriteDouble(sheep.Z);
            p.WriteByte(0); // zero LP_VEC3
            p.WriteByte(0); p.WriteByte(0); p.WriteByte(0);
            p.WriteVarInt(0);
        }, ct);
        await SendMetadataAsync(channel, sheep, ct);
    }

    public static Task SendMetadataAsync(PacketChannel channel,
        LivingEntity sheep, CancellationToken ct) =>
        channel.SendAsync(ClientboundSetEntityData, p =>
        {
            p.WriteVarInt(sheep.EntityId);
            p.WriteByte(9); p.WriteVarInt(3); p.WriteFloat(sheep.Health);
            p.WriteByte(18); p.WriteVarInt(0);
            p.WriteByte((byte)(sheep.Color | (sheep.Sheared ? 0x10 : 0)));
            p.WriteByte(0xFF);
        }, ct);

    public static Task SendRemoveAsync(PacketChannel channel, int id,
        CancellationToken ct) =>
        channel.SendAsync(ClientboundRemoveEntities, p =>
        {
            p.WriteVarInt(1); p.WriteVarInt(id);
        }, ct);
}
