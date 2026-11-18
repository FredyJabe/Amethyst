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
// Survival timings use the hardness table and the player's actual tool.
Check(MiningRules.RequiredMilliseconds(10, 0) >= 700 &&
    MiningRules.RequiredMilliseconds(10, 0) <= 800,
    "Dirt requires a timed hand-mining session");
Check(MiningRules.RequiredMilliseconds(1, 0) >
    MiningRules.RequiredMilliseconds(1, 1047) &&
    !MiningRules.IsHarvestable(MiningRules.ForState(1), 0) &&
    MiningRules.IsHarvestable(MiningRules.ForState(1), 1047),
    "Stone needs a pickaxe to drop cobblestone, correct tools mine faster");
Check(MiningRules.RequiredMilliseconds(88, 1047) == int.MaxValue,
    "Bedrock is not breakable");

string playerRoot = Path.Combine(Path.GetTempPath(), "amethyst-player-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(playerRoot);
try
{
    Guid uuid = Guid.NewGuid();
    var inventory = new PlayerInventory(playerRoot, uuid);
    Check(inventory.Give(10, 3) == 3 &&
        inventory.Held() == new ItemStack(10, 3), "Mined dirt enters Survival hotbar");
    Check(inventory.ConsumeHeld(false, 10) &&
        inventory.Held() == new ItemStack(10, 2), "Survival placement consumes one item");
    Check(inventory.ConsumeHeld(true, 10) &&
        inventory.Held() == new ItemStack(10, 2), "Creative placement does not consume an item");
    Check(!inventory.TryCreativeSet(36, 0, 64, true), "Reject invalid Creative stack");

    var (beforeSlots, _, revision) = inventory.Snapshot();
    Check(beforeSlots[36].Count == 2, "Inventory snapshots preserve stack counts");

    var clickWriter = new PacketWriter();
    clickWriter.WriteVarInt(0);
    clickWriter.WriteVarInt(revision);
    clickWriter.WriteInt16(36);
    clickWriter.WriteByte(0); // left click
    clickWriter.WriteVarInt(0);
    clickWriter.WriteVarInt(0); // no client claims for modified slots
    clickWriter.WriteBool(false); // no carried hashed slot
    var click = InventoryProtocol.ReadClick(new Packet(
        InventoryProtocol.ServerboundContainerClick, clickWriter.ToArray()));
    Check(inventory.ApplyClick(click) &&
        inventory.Held().Count == 0 &&
        inventory.Snapshot().Cursor.Count == 2,
        "Authoritative inventory click picks up a stack");
    using (var stream = new MemoryStream())
    {
        using var channel = new PacketChannel(stream);
        await InventoryProtocol.SendContentAsync(channel, inventory, CancellationToken.None);
        stream.Position = 0;
        Packet? contentPacket = await Protocol.ReadPacketAsync(stream, CancellationToken.None);
        Check(contentPacket is { Id: InventoryProtocol.ClientboundSetContent },
            "Inventory packet is framed with protocol 777 container ID");
    }
    var recovered = new PlayerInventory(playerRoot, uuid);
    Check(recovered.Snapshot().Cursor == new ItemStack(10, 2),
        "Inventory and cursor survive reconnect");
    Check(!recovered.ApplyClick(click), "Stale inventory revision cannot mutate items");
}
finally
{
    try { Directory.Delete(playerRoot, recursive: true); }
    catch (IOException) { }
}
Console.WriteLine("PASS: block codec, modes, item stacks, timed mining, edit overlay and persistence");
