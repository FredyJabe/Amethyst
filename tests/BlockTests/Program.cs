using Amethyst;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

// Packed positions round-trip signed 26/12/26-bit coordinates.
var writer = new PacketWriter();
writer.WritePosition(-17, -64, 26);
var reader = new PacketReader(writer.ToArray());
var position = reader.ReadPosition();
reader.RequireEnd();
Check(position == (-17, -64, 26), "signed BlockPos codec");

writer = new PacketWriter();
writer.WriteVarInt(0);
writer.WritePosition(-1, 79, -16);
writer.WriteByte(2);
writer.WriteVarInt(15);
var action = BlockInteractionProtocol.ReadAction(
    new Packet(BlockInteractionProtocol.ServerboundPlayerAction, writer.ToArray()));
Check(action == new PlayerBlockAction(0, -1, 79, -16, 2, 15), "Creative break decode");

writer = new PacketWriter();
writer.WriteVarInt(0);
writer.WritePosition(-1, 79, -16);
writer.WriteVarInt(1);
writer.WriteFloat(0.5f); writer.WriteFloat(1f); writer.WriteFloat(0.5f);
writer.WriteBool(false); writer.WriteBool(false);
writer.WriteVarInt(19);
var use = BlockInteractionProtocol.ReadUseOn(
    new Packet(BlockInteractionProtocol.ServerboundUseItemOn, writer.ToArray()));
Check(use.ValidHit && use.Sequence == 19 &&
    BlockInteractionProtocol.Adjacent(use.X, use.Y, use.Z, use.Face) == (-1, 80, -16),
    "Creative placement decode and face offset");

writer = new PacketWriter();
writer.WriteInt16(36);
writer.WriteVarInt(64);
writer.WriteVarInt(1); // vanilla stone
writer.WriteVarInt(0);
writer.WriteVarInt(0);
var slot = BlockInteractionProtocol.ReadCreativeSlot(
    new Packet(BlockInteractionProtocol.ServerboundCreativeSlot, writer.ToArray()));
Check(slot is { Slot: 36, ItemId: 1, PlainItem: true } &&
    CreativeBlockPalette.TryGetState(slot.ItemId, out ushort stone) && stone == 1,
    "Plain Creative stack and stone palette");

string temp = Path.Combine(Path.GetTempPath(), "amethyst-block-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var options = new ServerOptions
    {
        GeneratorVersion = 5,
        Seed = 624193,
        ViewDistance = 2,
        SaveGeneratedChunks = true,
        AsyncWorldSaves = false,
        WorldPath = Path.Combine(temp, "world")
    };
    ushort original;
    ushort replacement;
    byte[] before, after;
    await using (var world = new TerrainWorld(options))
    {
        original = world.GetBlockState(-1, 100, -1);
        replacement = original == 1 ? (ushort)14 : (ushort)1;
        before = world.SerializeSections(new ChunkPos(-1, -1));
        Check(world.TrySetBlockState(-1, 100, -1, replacement, out ushort previous) &&
            previous == original, "Editable world accepts negative chunk coordinates");
        Check(world.GetBlockState(-1, 100, -1) == replacement, "Edited block is authoritative");
        after = world.SerializeSections(new ChunkPos(-1, -1));
        Check(!before.SequenceEqual(after), "Chunk serialization includes edits");
        Check(!world.TrySetBlockState(-1, 100, -1, replacement, out _),
            "Repeated edit is a no-op");
        Check(!world.TrySetBlockState(-1, 400, -1, replacement, out _),
            "Outside world height rejected");
    }
    await using (var loaded = new TerrainWorld(options))
    {
        Check(loaded.GetBlockState(-1, 100, -1) == replacement,
            "Edited block survives world restart");
        Check(loaded.TrySetBlockState(-1, 100, -1, original, out _) &&
            loaded.GetBlockState(-1, 100, -1) == original,
            "Reverting to generated state removes overlay");
    }
    await using (var loaded = new TerrainWorld(options))
        Check(loaded.GetBlockState(-1, 100, -1) == original,
            "Reverted block survives another restart");
}
finally
{
    try { Directory.Delete(temp, recursive: true); }
    catch (IOException) { }
}
Console.WriteLine("PASS: block codec, Creative palette, edit overlay and persistence");
