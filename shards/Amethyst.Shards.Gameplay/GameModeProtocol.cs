namespace Amethyst;

// Java 26.3: numeric game mode IDs, game-event change notification, abilities,
// and the UPDATE_GAME_MODE action of Player Info Update.
public enum PlayerGameMode { Survival = 0, Creative = 1, Adventure = 2, Spectator = 3 }

public static class GameModeProtocol
{
    public const int RequiredOpLevel = 1;
    public const int ServerboundChangeGameMode = 0x05; // F3+F4 game-mode switcher
    public const int ClientboundGameEvent = 0x27;
    public const int ClientboundEntityEvent = 0x22; // 26.3 ClientboundEntityEventPacket
    public const int ClientboundPlayerAbilities = 0x41;
    public const int ClientboundPlayerInfoUpdate = 0x47;
    public const byte GameModeEvent = 3;
    public const byte UpdateGameModeAction = 0x04;

    public static bool CanChange(int opLevel) => opLevel >= RequiredOpLevel;

    // The unmodified 26.3 client checks its own vanilla GAMEMASTER (level 2)
    // permission before it will OPEN F3+F4. Our OP level 1 has the custom
    // permission to change modes. Report level 2 to the client for the UI,
    // but NEVER elevate the actual server-side OP level or its command tree.
    public static int ClientPermissionLevel(int actualOpLevel)
    {
        if (actualOpLevel is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(actualOpLevel));
        return actualOpLevel == 1 ? 2 : actualOpLevel;
    }

    // Clientbound entity_event payload is an Int entity ID followed by Byte.
    // Values 24..28 update LocalPlayer's level-based permissions (0..4).
    public static Task SendClientPermissionsAsync(PacketChannel channel, int entityId,
        int actualOpLevel, CancellationToken ct) =>
        channel.SendAsync(ClientboundEntityEvent, p =>
        {
            p.WriteInt32(entityId);
            p.WriteByte((byte)(24 + ClientPermissionLevel(actualOpLevel)));
        }, ct);

    public static bool TryParseMode(string input, out PlayerGameMode mode)
    {
        switch (input.ToLowerInvariant())
        {
            case "survival": mode = PlayerGameMode.Survival; return true;
            case "creative": mode = PlayerGameMode.Creative; return true;
            case "adventure": mode = PlayerGameMode.Adventure; return true;
            case "spectator": mode = PlayerGameMode.Spectator; return true;
            default: mode = default; return false;
        }
    }

    public static bool TryParseArguments(string raw, out PlayerGameMode mode, out string? targetName)
    {
        string[] parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        targetName = null;
        mode = default;
        if (parts.Length is < 1 or > 2 || !TryParseMode(parts[0], out mode)) return false;
        if (parts.Length == 2)
        {
            if (parts[1].Length is < 3 or > 16 ||
                !parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
            targetName = parts[1];
        }
        return true;
    }

    public static PlayerGameMode ParseSwitchRequest(Packet packet)
    {
        if (packet.Id != ServerboundChangeGameMode)
            throw new ArgumentException("Not a game mode switch request.", nameof(packet));
        var reader = packet.Reader();
        int mode = reader.ReadVarInt();
        reader.RequireEnd();
        if (mode is < 0 or > 3) throw new InvalidDataException("Invalid game mode ID.");
        return (PlayerGameMode)mode;
    }

    // Permission bits: 0x01 invulnerable, 0x02 flying, 0x04 may fly, 0x08 creative.
    // Spectators should start flying. This is a basic abilities implementation,
    // not a full server-side spectator/collision/interaction engine.
    public static byte AbilityFlags(PlayerGameMode mode) => mode switch
    {
        PlayerGameMode.Survival or PlayerGameMode.Adventure => 0x00,
        PlayerGameMode.Creative => 0x0D,
        PlayerGameMode.Spectator => 0x07,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static Task SendAbilitiesAsync(PacketChannel channel, PlayerGameMode mode, CancellationToken ct) =>
        channel.SendAsync(ClientboundPlayerAbilities, p =>
        {
            p.WriteByte(AbilityFlags(mode));
            p.WriteFloat(0.05f);  // flying speed
            p.WriteFloat(0.1f);   // field of view modifier / walking speed
        }, ct);

    public static async Task SendLocalChangeAsync(PacketChannel channel, PlayerGameMode mode, CancellationToken ct)
    {
        await channel.SendAsync(ClientboundGameEvent, p =>
        {
            p.WriteByte(GameModeEvent);
            p.WriteFloat((float)(int)mode);
        }, ct);
        await SendAbilitiesAsync(channel, mode, ct);
    }

    public static Task SendTabUpdateAsync(PacketChannel channel, Guid playerId, PlayerGameMode mode,
        CancellationToken ct) => channel.SendAsync(ClientboundPlayerInfoUpdate, p =>
        {
            p.WriteByte(UpdateGameModeAction);
            p.WriteVarInt(1);
            p.WriteUuid(playerId);
            p.WriteVarInt((int)mode);
        }, ct);
}
