using Amethyst;

static void Assert(bool pass, string description)
{
    if (!pass) throw new Exception($"FAILED: {description}");
    Console.WriteLine($"PASS: {description}");
}

Assert(!GameModeProtocol.CanChange(0), "Guests cannot change game modes");
Assert(GameModeProtocol.ClientPermissionLevel(0) == 0 &&
       GameModeProtocol.ClientPermissionLevel(1) == 2 &&
       GameModeProtocol.ClientPermissionLevel(2) == 2 &&
       GameModeProtocol.ClientPermissionLevel(3) == 3 &&
       GameModeProtocol.ClientPermissionLevel(4) == 4,
    "Vanilla client gamemaster switcher capability is presented only to OP1+ ");
for (int level = 1; level <= 4; level++)
    Assert(GameModeProtocol.CanChange(level), $"OP {level} can change game modes");

foreach (string label in new[] { "survival", "creative", "adventure", "spectator" })
    Assert(GameModeProtocol.TryParseArguments($"{label} ExamplePlayer", out _, out var target) &&
        target == "ExamplePlayer", $"/gamemode {label} ExamplePlayer parses");
Assert(GameModeProtocol.TryParseArguments("creative", out var creative, out var noTarget) &&
    creative == PlayerGameMode.Creative && noTarget is null, "Self gamemode parses");
Assert(!GameModeProtocol.TryParseArguments("unknown", out _, out _), "Unknown gamemode rejected");
Assert(!GameModeProtocol.TryParseArguments("creative some-player", out _, out _), "Invalid player rejected");
Assert(!GameModeProtocol.TryParseArguments("creative Alice Bob", out _, out _), "Extra arguments rejected");

Assert(GameModeProtocol.AbilityFlags(PlayerGameMode.Survival) == 0 &&
    GameModeProtocol.AbilityFlags(PlayerGameMode.Adventure) == 0, "Survival/adventure cannot fly");
Assert(GameModeProtocol.AbilityFlags(PlayerGameMode.Creative) == 0x0D, "Creative ability flags");
Assert(GameModeProtocol.AbilityFlags(PlayerGameMode.Spectator) == 0x07, "Spectator ability flags");

var sw = new PacketWriter();
sw.WriteVarInt(3);
Assert(GameModeProtocol.ParseSwitchRequest(new Packet(0x05, sw.ToArray())) == PlayerGameMode.Spectator,
    "26.3 F3+F4 switch packet uses game mode VarInt");
sw = new PacketWriter();
sw.WriteVarInt(4);
try
{
    GameModeProtocol.ParseSwitchRequest(new Packet(0x05, sw.ToArray()));
    throw new Exception("Invalid switch packet was accepted");
}
catch (InvalidDataException) { Console.WriteLine("PASS: Invalid switch mode rejected"); }

Guid uuid = Guid.Parse("12345678-1234-4234-8234-1234567890ab");
using var stream = new MemoryStream();
using (var channel = new PacketChannel(stream))
{
    await GameModeProtocol.SendClientPermissionsAsync(channel, 42, 1, CancellationToken.None);
    await GameModeProtocol.SendLocalChangeAsync(channel, PlayerGameMode.Spectator, CancellationToken.None);
    await GameModeProtocol.SendTabUpdateAsync(channel, uuid, PlayerGameMode.Spectator, CancellationToken.None);
}
stream.Position = 0;
Packet permission = (await Protocol.ReadPacketAsync(stream, CancellationToken.None))!.Value;
var permReader = permission.Reader();
Assert(permission.Id == 0x22 && permReader.ReadInt32() == 42 &&
    permReader.ReadByte() == 26 && permReader.Remaining == 0,
    "OP 1 is presented as client level 2 through entity_event 24+2");
Packet evt = (await Protocol.ReadPacketAsync(stream, CancellationToken.None))!.Value;
var r = evt.Reader();
Assert(evt.Id == 0x27 && r.ReadByte() == 3 && r.ReadFloat() == 3.0f && r.Remaining == 0,
    "Game Event 3 informs the local client of spectator mode");
Packet abilities = (await Protocol.ReadPacketAsync(stream, CancellationToken.None))!.Value;
r = abilities.Reader();
Assert(abilities.Id == 0x41 && r.ReadByte() == 0x07 && r.ReadFloat() == 0.05f &&
    r.ReadFloat() == 0.1f && r.Remaining == 0, "Player Abilities allows spectator flight");
Packet tab = (await Protocol.ReadPacketAsync(stream, CancellationToken.None))!.Value;
r = tab.Reader();
Assert(tab.Id == 0x47 && r.ReadByte() == 0x04 && r.ReadVarInt() == 1 &&
    r.ReadUuid() == uuid && r.ReadVarInt() == 3 && r.Remaining == 0,
    "Tab-list UPDATE_GAME_MODE targets the authenticated UUID");
Assert(stream.Position == stream.Length, "No trailing game mode packets");

// A de-op must immediately remove the client's elevated UI capability.
using (var revokeStream = new MemoryStream())
{
    using (var channel = new PacketChannel(revokeStream))
        await GameModeProtocol.SendClientPermissionsAsync(channel, 42, 0, CancellationToken.None);
    revokeStream.Position = 0;
    Packet permission0 = (await Protocol.ReadPacketAsync(revokeStream, CancellationToken.None))!.Value;
    var revokeReader = permission0.Reader();
    Assert(permission0.Id == 0x22 && revokeReader.ReadInt32() == 42 &&
        revokeReader.ReadByte() == 24 && revokeReader.Remaining == 0,
        "De-op restores vanilla client permission 0");
}
byte[] guestTree = CommandTree.Encode(0), level1Tree = CommandTree.Encode(1);
Assert(!HasCommand(guestTree, "gamemode") && HasCommand(level1Tree, "gamemode"),
    "Command is advertised only at OP level 1+");
Console.WriteLine("Game mode tests: PASS");

static bool HasCommand(byte[] bytes, string sought)
{
    var reader = new PacketReader(bytes);
    int count = reader.ReadVarInt();
    bool found = false;
    for (int i = 0; i < count; i++)
    {
        byte flags = reader.ReadByte();
        int children = reader.ReadVarInt();
        for (int j = 0; j < children; j++) reader.ReadVarInt();
        if ((flags & 0x08) != 0) reader.ReadVarInt();
        if ((flags & 3) is 1 or 2)
        {
            string label = reader.ReadString(32767);
            if ((flags & 3) == 1 && label == sought) found = true;
            if ((flags & 3) == 2)
            {
                // Existing Brigadier argument format: brigadier:string (parser 5), greedy (2).
                Assert(reader.ReadVarInt() == 5 && reader.ReadVarInt() == 2,
                    "Brigadier argument is greedy string");
            }
        }
        if ((flags & 0x10) != 0) reader.ReadString(32767);
    }
    Assert(reader.ReadVarInt() == 0 && reader.Remaining == 0, "Command tree parses completely");
    return found;
}
