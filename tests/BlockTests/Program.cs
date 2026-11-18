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
Check(action == new PlayerBlockAction(BlockInteractionProtocol.StartDestroyBlock,
    -1, 79, -16, 2, 15), "Creative break decode");
Check(BlockInteractionProtocol.StartDestroyBlock == 0 &&
    BlockInteractionProtocol.ChangeDestroyDirection == 1 &&
    BlockInteractionProtocol.AbortDestroyBlock == 2 &&
    BlockInteractionProtocol.StopDestroyBlock == 3 &&
    BlockInteractionProtocol.DropAllItems == 4 &&
    BlockInteractionProtocol.DropItem == 5 &&
    BlockInteractionProtocol.ReleaseUseItem == 6 &&
    BlockInteractionProtocol.SwapItemWithOffhand == 7 &&
    BlockInteractionProtocol.Stab == 8,
    "Java 26.3 nine player-action IDs are version-pinned");

writer = new PacketWriter();
writer.WriteVarInt(3); // 26.3 STOP, not item drop!
writer.WritePosition(6, 78, 0);
writer.WriteByte(1);
writer.WriteVarInt(2);
Check(BlockInteractionProtocol.ReadAction(
        new Packet(BlockInteractionProtocol.ServerboundPlayerAction, writer.ToArray()))
    == new PlayerBlockAction(BlockInteractionProtocol.StopDestroyBlock, 6, 78, 0, 1, 2),
    "Actual Survival STOP packet from client logs decodes as mining completion");

writer = new PacketWriter();
writer.WriteVarInt(2); // 26.3 ABORT, not STOP!
writer.WritePosition(5, 78, 0);
writer.WriteByte(0);
writer.WriteVarInt(0);
Check(BlockInteractionProtocol.ReadAction(
        new Packet(BlockInteractionProtocol.ServerboundPlayerAction, writer.ToArray()))
    == new PlayerBlockAction(BlockInteractionProtocol.AbortDestroyBlock, 5, 78, 0, 0, 0),
    "Actual Survival ABORT packet from client logs decodes as cancellation");

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
Check(MiningRules.ForState(8).DropItem == 55,
    "Grass yields dirt in the simple Survival drop table");
Check(MiningRules.IsHarvestable(MiningRules.ForState(6863), 1047),
    "Diamond ore can be harvested using a sufficiently strong pickaxe");
Check(CreativeBlockPalette.TryGetState(163, 1, out ushort verticalLog) &&
    verticalLog == 140 &&
    CreativeBlockPalette.TryGetState(163, 4, out ushort sidewaysLog) &&
    sidewaysLog == 139, "Log axis follows the placement face");
Check(CreativeBlockPalette.TryGetState(223, out ushort mangroveLeaves) &&
    mangroveLeaves >= 491 && mangroveLeaves <= 518,
    "Vanilla mangrove leaves are supported");

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
// The Java client uses a 70%-of-expected-progress STOP threshold. Breaking a
// second block must not resurrect the first via a stale predicted-state ACK.
string miningRoot = Path.Combine(Path.GetTempPath(),
    "amethyst-sequential-mining-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(miningRoot);
try
{
    var settings = new ServerOptions
    {
        GeneratorVersion = 5,
        Seed = 123456789,
        ViewDistance = 2,
        SaveGeneratedChunks = true,
        AsyncWorldSaves = false,
        WorldPath = Path.Combine(miningRoot, "world"),
        OperatorsFile = Path.Combine(miningRoot, "ops.yaml"),
        DefaultGameMode = "survival"
    };

    // Default gamemode must be configurable without modifying world identity.
    Check(new ServerOptions().DefaultGameMode == "creative",
        "Default config preserves existing Creative behavior");
    string yaml = Path.Combine(miningRoot, "server.yaml");
    foreach (string mode in new[] { "survival", "creative", "adventure", "spectator" })
    {
        File.WriteAllText(yaml, $"defaultGameMode: {mode}\n");
        Check(ServerOptions.Load(yaml).DefaultGameMode == mode,
            $"YAML accepts default gamemode {mode}");
    }
    File.WriteAllText(yaml, "defaultGameMode: invalid\n");
    bool invalidModeRejected = false;
    try { _ = ServerOptions.Load(yaml); }
    catch (ArgumentException) { invalidModeRejected = true; }
    Check(invalidModeRejected, "Invalid default gamemode fails validation");

    const int blockY = 81, blockZ = 1;
    await using (var world = new TerrainWorld(settings))
    {
        Check(world.TrySetBlockState(1, blockY, blockZ, 10, out _) &&
            world.TrySetBlockState(2, blockY, blockZ, 10, out _),
            "Prepare adjacent dirt blocks for consecutive Survival mining");

        using var outgoing = new MemoryStream();
        using var channel = new PacketChannel(outgoing);
        var player = new ConnectedPlayer(Guid.NewGuid(), "MiningTester",
            Array.Empty<ProfileProperty>(), 1, channel, world,
            Path.Combine(miningRoot, "playerdata"));
        player.MarkReady();
        player.SetGameMode(PlayerGameMode.Survival);
        // The handler does not use the packet bus when invoked directly.
        var game = new MinecraftServer(settings, world, null!, world);

        for (int x = 1; x <= 2; x++)
        {
            int startSequence = x * 2 - 1, stopSequence = x * 2;
            int startOffset = checked((int)outgoing.Length);
            await game.HandleBlockActionAsync(player,
                new PlayerBlockAction(BlockInteractionProtocol.StartDestroyBlock, x, blockY, blockZ, 1, startSequence),
                CancellationToken.None);
            // START has no predicted removal in Survival. Do not send a stale
            // block-state correction; only acknowledge the action.
            byte[] beforeStop = outgoing.ToArray().AsSpan(startOffset).ToArray();
            using (var startStream = new MemoryStream(beforeStop))
            {
                var packets = new List<Packet>();
                Packet? packet;
                while ((packet = await Protocol.ReadPacketAsync(startStream,
                    CancellationToken.None)) is { } read) packets.Add(read);
                Check(!packets.Any(p => p.Id == BlockInteractionProtocol.ClientboundBlockUpdate),
                    "Survival START does not send a stale block correction");
            }

            // 520ms is over the 70% server threshold for 750ms dirt, but
            // below the old exact 100% threshold which caused client rollbacks.
            await Task.Delay(520);
            await game.HandleBlockActionAsync(player,
                new PlayerBlockAction(BlockInteractionProtocol.StopDestroyBlock, x, blockY, blockZ, 1, stopSequence),
                CancellationToken.None);
            Check(world.GetBlockState(x, blockY, blockZ) == 0,
                $"Survival STOP authoritatively removed block {x}");
        }
        Check(world.GetBlockState(1, blockY, blockZ) == 0 &&
              world.GetBlockState(2, blockY, blockZ) == 0,
            "First block remains air after breaking second");

        var updates = new List<(int X, int Y, int Z, int State, int Index)>();
        var acknowledgements = new List<(int Sequence, int Index)>();
        using (var stream = new MemoryStream(outgoing.ToArray()))
        {
            int index = 0;
            Packet? packet;
            while ((packet = await Protocol.ReadPacketAsync(stream, CancellationToken.None)) is { } read)
            {
                PacketReader fields = read.Reader();
                if (read.Id == BlockInteractionProtocol.ClientboundBlockUpdate)
                {
                    var (x, y, z) = fields.ReadPosition();
                    int state = fields.ReadVarInt();
                    fields.RequireEnd();
                    updates.Add((x, y, z, state, index));
                }
                if (read.Id == BlockInteractionProtocol.ClientboundChangedAck)
                {
                    int sequence = fields.ReadVarInt();
                    fields.RequireEnd();
                    acknowledgements.Add((sequence, index));
                }
                index++;
            }
        }
        Check(updates.Count == 2 &&
            updates[0] is (1, blockY, blockZ, 0, _) &&
            updates[1] is (2, blockY, blockZ, 0, _),
            "Both successful breaks send authoritative air, never stale dirt");
        Check(acknowledgements.Count == 4 &&
            acknowledgements[1].Sequence == 2 &&
            acknowledgements[3].Sequence == 4 &&
            updates[0].Index < acknowledgements[1].Index &&
            updates[1].Index < acknowledgements[3].Index,
            "Confirmed block updates precede their prediction acknowledgements");

        // When chunk streaming blocks the receive loop, START/STOP can be
        // processed back-to-back despite spanning many real client ticks.
        // Their client_tick_end markers retain the correct elapsed progress.
        Check(world.TrySetBlockState(3, blockY, blockZ, 10, out _),
            "Prepare a dirt block for queued-tick mining");
        await game.HandleBlockActionAsync(player,
            new PlayerBlockAction(BlockInteractionProtocol.StartDestroyBlock, 3, blockY, blockZ, 1, 5),
            CancellationToken.None);
        for (int tick = 0; tick < 14; tick++)
            player.AdvanceClientTick();
        await game.HandleBlockActionAsync(player,
            new PlayerBlockAction(BlockInteractionProtocol.ChangeDestroyDirection,
                3, blockY, blockZ, 2, 0), CancellationToken.None);
        Check(world.GetBlockState(3, blockY, blockZ) == 10,
            "CHANGE_DESTROY_DIRECTION does not destroy or cancel the active mining target");
        await game.HandleBlockActionAsync(player,
            new PlayerBlockAction(BlockInteractionProtocol.StopDestroyBlock, 3, blockY, blockZ, 1, 6),
            CancellationToken.None);
        Check(world.GetBlockState(3, blockY, blockZ) == 0,
            "Queued valid client ticks allow a finished dig without relying on wall-clock time");

        // A status=2 ABORT with sequence=0 (exactly like the user's real
        // client log) must clear the mining session rather than mining air.
        Check(world.TrySetBlockState(5, blockY, blockZ, 10, out _),
            "Prepare a dirt block for abort/late-stop validation");
        await game.HandleBlockActionAsync(player,
            new PlayerBlockAction(BlockInteractionProtocol.StartDestroyBlock,
                5, blockY, blockZ, 1, 8), CancellationToken.None);
        await game.HandleBlockActionAsync(player,
            new PlayerBlockAction(BlockInteractionProtocol.AbortDestroyBlock,
                5, blockY, blockZ, 0, 0), CancellationToken.None);
        for (int tick = 0; tick < 14; tick++) player.AdvanceClientTick();
        await game.HandleBlockActionAsync(player,
            new PlayerBlockAction(BlockInteractionProtocol.StopDestroyBlock,
                5, blockY, blockZ, 1, 9), CancellationToken.None);
        Check(world.GetBlockState(5, blockY, blockZ) == 10,
            "ABORT cancels a dig and a subsequent STOP cannot break it");

        // An efficient tool can finish a soft block on the FIRST client tick.
        // Vanilla then sends only START (no STOP); previously the server
        // incorrectly left the block in the world indefinitely.
        Check(MiningRules.RequiredMilliseconds(267, 1054) <= 50,
            "Diamond hoe instamines leaves in one client tick");
        Check(player.Inventory.TryCreativeSet(36, 1054, 1, true),
            "Prepare a simple diamond hoe stack for the one-tick test");
        Check(world.TrySetBlockState(4, blockY, blockZ, 267, out _),
            "Prepare a nearby leaves block");
        int beforeInstant = checked((int)outgoing.Length);
        await game.HandleBlockActionAsync(player,
            new PlayerBlockAction(BlockInteractionProtocol.StartDestroyBlock, 4, blockY, blockZ, 1, 7),
            CancellationToken.None);
        Check(world.GetBlockState(4, blockY, blockZ) == 0,
            "One-tick Survival START destroys leaves without waiting for STOP");
        Check(world.GetBlockState(1, blockY, blockZ) == 0 &&
            world.GetBlockState(2, blockY, blockZ) == 0,
            "Previous Survival breaks stay committed after one-tick mining");
        using (var instantStream = new MemoryStream(outgoing.ToArray().AsSpan(beforeInstant).ToArray()))
        {
            Packet? update = await Protocol.ReadPacketAsync(instantStream, CancellationToken.None);
            Check(update is { Id: BlockInteractionProtocol.ClientboundBlockUpdate },
                "One-tick break sends an authoritative block update");
            if (update is { } airUpdate)
            {
                PacketReader fields = airUpdate.Reader();
                var instantPosition = fields.ReadPosition();
                Check(instantPosition == (4, blockY, blockZ) && fields.ReadVarInt() == 0,
                    "One-tick break publishes air for exactly the mined position");
            }
            // With leaves drops deliberately unsupported, only an ACK follows.
            Packet? ack = await Protocol.ReadPacketAsync(instantStream, CancellationToken.None);
            Check(ack is { Id: BlockInteractionProtocol.ClientboundChangedAck } &&
                ack.Value.Reader().ReadVarInt() == 7,
                "One-tick START sequence is acknowledged after authoritative air");
        }
    }
    await using (var world = new TerrainWorld(settings))
        Check(world.GetBlockState(1, blockY, blockZ) == 0 &&
              world.GetBlockState(2, blockY, blockZ) == 0 &&
              world.GetBlockState(3, blockY, blockZ) == 0 &&
              world.GetBlockState(4, blockY, blockZ) == 0,
            "Timed, queued-tick and one-tick Survival breaks persist after world restart");
}
finally
{
    try { Directory.Delete(miningRoot, recursive: true); }
    catch (IOException) { }
}
Console.WriteLine("PASS: consecutive Survival breaks, client reconciliation, default gamemode config");

Console.WriteLine("PASS: block codec, modes, item stacks, timed mining, edit overlay and persistence");
