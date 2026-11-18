using System.Buffers.Binary;
using System.Security.Cryptography;
using Amethyst.Shards;

namespace Amethyst;

public static class SpawnPackets
{
    public const int TeleportId = 1;

    public static async Task SendAsync(ConnectedPlayer player, IShardWorld world,
        ServerOptions settings, WorldClock clock, CancellationToken ct)
    {
        PacketChannel channel = player.Connection;
        int dimensionType = VanillaRegistries.IndexOf("minecraft:dimension_type", world.DimensionId);
        await channel.SendAsync(0x32, p =>
        {
            p.WriteInt32(player.EntityId);
            p.WriteBool(false); // hardcore
            p.WriteVarInt(1); p.WriteString(world.DimensionId);
            p.WriteVarInt(settings.MaxPlayers);
            p.WriteVarInt(settings.ViewDistance);
            p.WriteVarInt(settings.ViewDistance);
            p.WriteBool(false); // debug info
            p.WriteBool(true);  // death screen
            p.WriteBool(false); // limited crafting
            p.WriteVarInt(dimensionType);
            p.WriteString(world.DimensionId);
            p.WriteInt64(HashSeed(world.Seed)); // vanilla-style hashed seed
            p.WriteVarInt((int)player.GameMode); // current player game mode
            p.WriteVarInt(0); // previous gamemode not set
            p.WriteBool(false); // is debug world
            p.WriteBool(false); // procedural terrain, not a flat preset
            p.WriteBool(false); // death location absent
            p.WriteVarInt(0);   // portal cooldown
            p.WriteVarInt(world.SeaLevel); // generated sea level
            p.WriteBool(settings.OnlineMode); // online/secure profile flag
            p.WriteBool(false); // secure chat enforcement
        }, ct);
        await GameModeProtocol.SendAbilitiesAsync(channel, player.GameMode, ct);
        await clock.SendAsync(channel, ct);
        await channel.SendAsync(0x61, p => p.WriteVarInt(settings.ViewDistance), ct);
        await channel.SendAsync(0x71, p => p.WriteVarInt(settings.ViewDistance), ct);
        await channel.SendAsync(0x63, p =>
        {
            p.WriteString(world.DimensionId);
            p.WritePosition(0, (int)world.SpawnYPosition, 0);
            p.WriteFloat(0); p.WriteFloat(0);
        }, ct);
        // Game Event: start waiting for level chunks
        await channel.SendAsync(0x27, p => { p.WriteByte(13); p.WriteFloat(0); }, ct);
        // Establish the client's camera position BEFORE the first chunk data.
        // Otherwise some clients build terrain render sections for their default
        // pre-teleport position and leave already-loaded chunks invisible.
        PlayerPose initial = player.Pose;
        await SendPositionAsync(channel, TeleportId, initial.X, initial.Y,
            initial.Z, initial.Yaw, initial.Pitch, ct);
        // Stream the chunks for the restored location, not the world spawn.
        await player.View.UpdateAsync(channel, initial, ct);
    }

    private static long HashSeed(long seed)
    {
        Span<byte> raw = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(raw, seed);
        byte[] sha = SHA256.HashData(raw);
        return BinaryPrimitives.ReadInt64BigEndian(sha);
    }

    public static Task SendPositionAsync(PacketChannel channel, int teleportId,
        double x, double y, double z, CancellationToken ct) =>
        SendPositionAsync(channel, teleportId, x, y, z, 0, 0, ct);

    public static Task SendPositionAsync(PacketChannel channel, int teleportId,
        double x, double y, double z, float yaw, float pitch, CancellationToken ct) =>
        channel.SendAsync(0x49, p =>
        {
            p.WriteVarInt(teleportId);
            p.WriteDouble(x); p.WriteDouble(y); p.WriteDouble(z);
            p.WriteDouble(0); p.WriteDouble(0); p.WriteDouble(0);
            p.WriteFloat(yaw); p.WriteFloat(pitch);
            p.WriteInt32(0); // absolute position / rotation
        }, ct);
}