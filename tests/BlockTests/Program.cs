using Amethyst;
using Amethyst.BuiltinShards;
using Amethyst.Shards;

DirectoryInfo? solutionRoot = new(AppContext.BaseDirectory);
while (solutionRoot is not null &&
    !File.Exists(Path.Combine(solutionRoot.FullName, "Amethyst.slnx")))
    solutionRoot = solutionRoot.Parent;
if (solutionRoot is null)
    throw new InvalidOperationException("Amethyst.slnx not found above tests.");
string itemCatalogPath = Path.Combine(solutionRoot.FullName,
    "shards", "Amethyst.Shards.Items", "items-26.3.yml");
var itemsCatalog = new ItemCatalog(itemCatalogPath);
string recipesFilePath = Path.Combine(solutionRoot.FullName,
    "shards", "Amethyst.Shards.Items", "recipes-26.3.yml");
var recipesCatalog = new RecipeCatalog(itemsCatalog, recipesFilePath);

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

// The Items Shard is the sole version-pinned source of item identities
// and stacking rules, including items that may only stack to one or 16.
Amethyst.Shards.ShardItemDefinition diamondPickaxe = default;
Amethyst.Shards.ShardItemDefinition pearls = default;
Check(itemsCatalog.Count == 1658 &&
    itemsCatalog.TryGetByIdentifier("minecraft:diamond_pickaxe", out diamondPickaxe) &&
    diamondPickaxe.MaxStackSize == 1 &&
    itemsCatalog.TryGetByIdentifier("minecraft:ender_pearl", out pearls) &&
    pearls.MaxStackSize == 16 &&
    itemsCatalog.TryGetById(405, out var craftingDefinition) &&
    craftingDefinition.Identifier == "minecraft:crafting_table" &&
    craftingDefinition.IsBlockItem,
    "Items Shard exposes full vanilla 26.3 item data to gameplay");
var stackInventory = new PlayerInventory(null, Guid.NewGuid(), itemsCatalog);
Check(!stackInventory.TryCreativeSet(36, diamondPickaxe.ProtocolId, 2, true) &&
    stackInventory.TryCreativeSet(36, diamondPickaxe.ProtocolId, 1, true),
    "Creative cannot create illegal stacks of non-stackable tools");
Check(stackInventory.Give(diamondPickaxe.ProtocolId, 2) == 2 &&
    stackInventory.Snapshot().Slots.Count(s =>
        s.ItemId == diamondPickaxe.ProtocolId && s.Count == 1) == 3,
    "Ground item pickup distributes non-stackable tools to separate slots");
Check(!stackInventory.TryCreativeSet(37, pearls.ProtocolId, 17, true) &&
    stackInventory.TryCreativeSet(37, pearls.ProtocolId, 16, true),
    "Items Shard's 16-item cap applies to creative and inventory");
Check(!itemsCatalog.IsValidStack(99999, 1) &&
    !itemsCatalog.IsValidStack(0, 1) &&
    itemsCatalog.IsValidStack(0, 0),
    "Unknown IDs and non-empty air stacks are never valid");

// Mythic-style YAML: each root ID overrides one vanilla item or names a
// custom alias backed by a vanilla protocol item. Never mint new IDs.
string yamlTestRoot = Path.Combine(Path.GetTempPath(),
    "amethyst-item-yaml-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(yamlTestRoot);
try
{
    string overrides = Path.Combine(yamlTestRoot, "overrides");
    Directory.CreateDirectory(overrides);
    File.WriteAllText(Path.Combine(overrides, "weapons.yml"), """
        diamond_pickaxe:
          Id: minecraft:diamond_pickaxe
          Display: "<aqua>Test Pickaxe</aqua>"
          ItemName: "Mining Tool"
          Lore:
            - "Configurable item"
            - "Defined in YAML"
          Options:
            Hidden: true
        LegendaryPickaxe:
          Id: diamond_pickaxe
          Display: "<gold>Legendary Pickaxe</gold>"
          Lore:
            - "Alias sharing vanilla protocol ID"
        """);
    string more = Path.Combine(overrides, "other");
    Directory.CreateDirectory(more);
    File.WriteAllText(Path.Combine(more, "stacks.yaml"), """
        ender_pearl:
          Id: ender_pearl
          Options:
            StackSize: 8
        405:
          Id: minecraft:crafting_table
          Display: "Workshop Table"
          Options:
            BlockItem: true
        """);
    var configured = new ItemCatalog(itemCatalogPath, overrides);
    Amethyst.Shards.ShardItemDefinition updatedPickaxe = default;
    Check(configured.Count == 1658 &&
        configured.TryGetByIdentifier("diamond_pickaxe", out updatedPickaxe) &&
        updatedPickaxe.Display == "<aqua>Test Pickaxe</aqua>" &&
        updatedPickaxe.ItemName == "Mining Tool" &&
        updatedPickaxe.Lore?.Count == 2 && updatedPickaxe.Hidden,
        "YAML overrides existing item display name, lore, and hidden metadata");
    Check(configured.TryGetByIdentifier("LegendaryPickaxe", out var alias) &&
        alias.ProtocolId == updatedPickaxe.ProtocolId &&
        alias.Identifier == "amethyst:legendarypickaxe" &&
        alias.Display == "<gold>Legendary Pickaxe</gold>" &&
        configured.All.Count == 1658,
        "Mythic-style root custom name aliases a known vanilla material, not a new protocol ID");
    Check(configured.TryGetByIdentifier("ender_pearl", out var smallPearl) &&
        smallPearl.MaxStackSize == 8 &&
        configured.IsValidStack(smallPearl.ProtocolId, 8) &&
        !configured.IsValidStack(smallPearl.ProtocolId, 9) &&
        configured.TryGetById(405, out var workshop) &&
        workshop.Display == "Workshop Table",
        "YAML configuration overrides actual inventory limits and numeric-root item metadata");
    var yamlInventory = new PlayerInventory(null, Guid.NewGuid(), configured);
    Check(yamlInventory.TryCreativeSet(36, smallPearl.ProtocolId, 8, true) &&
        !yamlInventory.TryCreativeSet(37, smallPearl.ProtocolId, 9, true),
        "Configured stack limit is used by Gameplay's inventory transaction validator");

    // Invalid configurations fail fast instead of silently starting with
    // partially applied definitions or ambiguous duplicate overrides.
    void InvalidYaml(string filename, string source, string label)
    {
        string dir = Path.Combine(yamlTestRoot, "invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, filename), source);
        bool rejected = false;
        try { _ = new ItemCatalog(itemCatalogPath, dir); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, label);
    }
    InvalidYaml("wrong-id.yml", """
        diamond_pickaxe:
          Id: minecraft:stone
        """, "YAML cannot change an existing item's vanilla material identity");
    InvalidYaml("wrong-protocol.yml", """
        diamond_pickaxe:
          ProtocolId: 405
        """, "YAML cannot reassign vanilla protocol IDs");
    InvalidYaml("max-overflow.yaml", """
        diamond_pickaxe:
          MaxStackSize: 64
        """, "YAML cannot exceed a vanilla item's maximum stack size");
    InvalidYaml("unknown-field.yml", """
        stone:
          UnimplementedFeature: true
        """, "Unknown item configuration fields fail loudly");
    InvalidYaml("unknown-alias.yml", """
        MysterySword:
          Id: minecraft:not_a_material
        """, "Custom root IDs require a real base vanilla material");
    InvalidYaml("alias-max.yml", """
        MythicSword:
          Id: diamond_sword
          MaxStackSize: 32
        """, "Aliases cannot change physical stack rules without a per-stack identity");
    InvalidYaml("duplicate.yml", """
        stone:
          Display: First
        stone:
          Display: Second
        """, "Duplicate YAML item roots are rejected");
    InvalidYaml("conflicting-limits.yml", """
        stone:
          MaxStackSize: 8
          Options:
            StackSize: 4
        """, "Conflicting MaxStackSize and Options.StackSize are rejected");

    string repeated = Path.Combine(yamlTestRoot, "repeated");
    Directory.CreateDirectory(repeated);
    File.WriteAllText(Path.Combine(repeated, "a.yml"), "stone:\n  Display: One\n");
    File.WriteAllText(Path.Combine(repeated, "b.yaml"), "stone:\n  Display: Two\n");
    bool duplicateRejected = false;
    try { _ = new ItemCatalog(itemCatalogPath, repeated); }
    catch (InvalidDataException) { duplicateRejected = true; }
    Check(duplicateRejected, "Overlapping definitions across YAML files are rejected");
    string numericOverlap = Path.Combine(yamlTestRoot, "numeric-alias-collision");
    Directory.CreateDirectory(numericOverlap);
    File.WriteAllText(Path.Combine(numericOverlap, "a.yml"),
        "405:\n  Display: Workshop\n");
    File.WriteAllText(Path.Combine(numericOverlap, "b.yml"),
        "crafting_table:\n  Display: Workbench\n");
    bool numericCollisionRejected = false;
    try { _ = new ItemCatalog(itemCatalogPath, numericOverlap); }
    catch (InvalidDataException) { numericCollisionRejected = true; }
    Check(numericCollisionRejected,
        "Numeric and named roots for the same vanilla protocol ID cannot override twice");
}
finally
{
    try { Directory.Delete(yamlTestRoot, recursive: true); }
    catch (IOException) { }
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
Check(CreativeBlockPalette.TryGetState(54, out ushort grassBlock) && grassBlock == 9 &&
    CreativeBlockPalette.TryGetState(54, 1, out ushort grassOnTop) && grassOnTop == 9,
    "Minecraft 26.3 grass block item 54 places non-snowy block state 9");
Check(CreativeBlockPalette.TryGetState(405, out ushort craftingTable) && craftingTable == 6866 &&
    CreativeBlockPalette.TryGetState(405, 1, out ushort tableOnTop) && tableOnTop == 6866,
    "Minecraft 26.3 crafting table item 405 places block state 6866");
Check(MiningRules.ForState(6866).DropItem == 405,
    "Survival breaking a crafting table returns its own item");

string playerRoot = Path.Combine(Path.GetTempPath(), "amethyst-player-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(playerRoot);
try
{
    Guid uuid = Guid.NewGuid();
    var inventory = new PlayerInventory(playerRoot, uuid, itemsCatalog);
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
    var recovered = new PlayerInventory(playerRoot, uuid, itemsCatalog);
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
            Path.Combine(miningRoot, "playerdata"), itemsCatalog);
        player.MarkReady();
        player.SetGameMode(PlayerGameMode.Survival);
        // The handler does not use the packet bus when invoked directly.
        var game = new MinecraftServer(settings, world, null!, world,
            items: itemsCatalog, recipes: recipesCatalog);

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

        // Mined loot must remain in the world until a player physically
        // collects it, rather than being granted at mining time.
        GroundItem[] firstDrops = game.GroundItemsForTests;
        Check(firstDrops.Length == 2 && firstDrops.All(i => i.ItemId == 55 && i.Count == 1) &&
            player.Inventory.Held().Count == 0,
            "Both dirt drops remain ground entities; mining does not grant inventory items");

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

        // Exercise the same block-use handler used by connected players.
        // Both blocks must work in both game modes, and Survival must
        // consume exactly one held item while Creative consumes none.
        var placements = new (PlayerGameMode Mode, int ItemId, ushort State)[]
        {
            (PlayerGameMode.Creative, 54, 9),
            (PlayerGameMode.Creative, 405, 6866),
            (PlayerGameMode.Survival, 54, 9),
            (PlayerGameMode.Survival, 405, 6866)
        };
        for (int index = 0; index < placements.Length; index++)
        {
            var test = placements[index];
            int blockX = index + 1;
            const int baseZ = 4, groundY = 81, placedY = 82;
            world.TrySetBlockState(blockX, groundY, baseZ, 10, out _);
            world.TrySetBlockState(blockX, placedY, baseZ, 0, out _);
            Check(world.GetBlockState(blockX, groundY, baseZ) == 10 &&
                world.GetBlockState(blockX, placedY, baseZ) == 0,
                "Test fixture has a solid support block and an empty target");
            player.SetGameMode(test.Mode);
            Check(player.Inventory.TryCreativeSet(36, test.ItemId, 3, true),
                "Prepare a plain hotbar stack for gameplay placement test");
            await game.HandleBlockUseAsync(player,
                new BlockUseOn(0, blockX, groundY, baseZ, 1, 20 + index, true),
                CancellationToken.None);
            Check(world.GetBlockState(blockX, placedY, baseZ) == test.State,
                $"{test.Mode} places item {test.ItemId} as state {test.State}");
            Check(player.Inventory.Held() == new ItemStack(test.ItemId,
                    test.Mode == PlayerGameMode.Survival ? 2 : 3),
                $"{test.Mode} applies correct inventory consumption for item {test.ItemId}");
        }
    }
    await using (var world = new TerrainWorld(settings))
    {
        Check(world.GetBlockState(1, blockY, blockZ) == 0 &&
              world.GetBlockState(2, blockY, blockZ) == 0 &&
              world.GetBlockState(3, blockY, blockZ) == 0 &&
              world.GetBlockState(4, blockY, blockZ) == 0,
            "Timed, queued-tick and one-tick Survival breaks persist after world restart");
        Check(world.GetBlockState(1, 82, 4) == 9 &&
              world.GetBlockState(2, 82, 4) == 6866 &&
              world.GetBlockState(3, 82, 4) == 9 &&
              world.GetBlockState(4, 82, 4) == 6866,
            "Grass blocks and crafting tables persist after Creative/Survival placement and restart");
    }
}
finally
{
    try { Directory.Delete(miningRoot, recursive: true); }
    catch (IOException) { }
}
Console.WriteLine("PASS: consecutive Survival breaks, client reconciliation, default gamemode config");

// Verify protocol 777 dropped-item packets with Mojang's entity type and
// metadata serializer, plus the server-side collision/pickup lifecycle.
string dropsRoot = Path.Combine(Path.GetTempPath(), "amethyst-ground-items-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dropsRoot);
try
{
    int entitySequence = 50;
    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var drops = new GroundItemStore(dropsRoot, "test-world", () => ++entitySequence, itemsCatalog);
    Check(drops.TrySpawn(55, 3, 10.5, 65.4, 10.5, out GroundItem firstDrop, now),
        "Breaking a block can create a persistent physical item entity");
    Check(drops.Snapshot() is [{ ItemId: 55, Count: 3 }],
        "Item stack remains in world rather than inventory");

    using (var stream = new MemoryStream())
    {
        using var channel = new PacketChannel(stream);
        await DroppedItemProtocol.SendSpawnAsync(channel, firstDrop, CancellationToken.None);
        await DroppedItemProtocol.SendPositionAsync(channel,
            firstDrop with { Y = 64.4, VelocityY = -0.14, OnGround = false },
            CancellationToken.None);
        await DroppedItemProtocol.SendPickupAsync(channel, firstDrop.EntityId, 2, 1,
            CancellationToken.None);
        await DroppedItemProtocol.SendRemoveAsync(channel, firstDrop.EntityId,
            CancellationToken.None);
        stream.Position = 0;
        Packet? spawn = await Protocol.ReadPacketAsync(stream, CancellationToken.None);
        Check(spawn is { Id: DroppedItemProtocol.AddEntity },
            "Item spawn uses Java 26.3 add_entity");
        if (spawn is { } entityPacket)
        {
            PacketReader pr = entityPacket.Reader();
            Check(pr.ReadVarInt() == firstDrop.EntityId &&
                pr.ReadUuid() == firstDrop.Uuid &&
                pr.ReadVarInt() == DroppedItemProtocol.ItemEntityType &&
                pr.ReadDouble() == 10.5 &&
                pr.ReadDouble() == 65.4 &&
                pr.ReadDouble() == 10.5 &&
                pr.ReadByte() == 0, "Item entity spawn contains ID 72 and correct position");
            pr.ReadByte(); pr.ReadByte(); pr.ReadByte(); // orientation
            Check(pr.ReadVarInt() == 0, "Item spawn has no auxiliary data");
            pr.RequireEnd();
        }
        Packet? metadata = await Protocol.ReadPacketAsync(stream, CancellationToken.None);
        Check(metadata is { Id: DroppedItemProtocol.SetEntityData },
            "Item stack is sent as entity metadata");
        if (metadata is { } stackPacket)
        {
            PacketReader pr = stackPacket.Reader();
            Check(pr.ReadVarInt() == firstDrop.EntityId &&
                pr.ReadByte() == 8 && pr.ReadVarInt() == 7 &&
                pr.ReadVarInt() == 3 && pr.ReadVarInt() == 55 &&
                pr.ReadVarInt() == 0 && pr.ReadVarInt() == 0 &&
                pr.ReadByte() == 0xFF, "Item metadata index 8 / serializer 7 is correctly framed");
            pr.RequireEnd();
        }
        Packet? motion = await Protocol.ReadPacketAsync(stream, CancellationToken.None);
        Check(motion is { Id: DroppedItemProtocol.TeleportEntity },
            "Item motion uses 26.3 entity teleport packet");
        if (motion is { } movePacket)
        {
            PacketReader pr = movePacket.Reader();
            Check(pr.ReadVarInt() == firstDrop.EntityId &&
                pr.ReadDouble() == 10.5 && pr.ReadDouble() == 64.4 &&
                pr.ReadDouble() == 10.5 && pr.ReadDouble() == 0 &&
                pr.ReadDouble() == -0.14 && pr.ReadDouble() == 0 &&
                pr.ReadFloat() == 0 && pr.ReadFloat() == 0 &&
                pr.ReadInt32() == 0 && pr.ReadByte() == 0,
                "Teleport payload conveys item position and velocity");
            pr.RequireEnd();
        }
        Packet? pickup = await Protocol.ReadPacketAsync(stream, CancellationToken.None);
        Check(pickup is { Id: DroppedItemProtocol.TakeItemEntity },
            "Pickup animation packet is emitted");
        if (pickup is { } takePacket)
        {
            PacketReader pr = takePacket.Reader();
            Check(pr.ReadVarInt() == firstDrop.EntityId &&
                pr.ReadVarInt() == 2 && pr.ReadVarInt() == 1,
                "Pickup names the dropped entity, collector and taken count");
            pr.RequireEnd();
        }
        Packet? removal = await Protocol.ReadPacketAsync(stream, CancellationToken.None);
        Check(removal is { Id: DroppedItemProtocol.RemoveEntities },
            "Consumed item entity is removed from other clients");
        if (removal is { } removePacket)
        {
            PacketReader pr = removePacket.Reader();
            Check(pr.ReadVarInt() == 1 && pr.ReadVarInt() == firstDrop.EntityId,
                "Remove entities packet contains the correct ID");
            pr.RequireEnd();
        }
    }

    var ground = new TestFlatBlockWorld();
    for (int tick = 1; tick <= 25; tick++)
        drops.Simulate(ground, now + tick * 50);
    Check(drops.Snapshot() is [{ OnGround: true }],
        "Loose items fall under gravity until hitting world blocks");
    var inventory = new PlayerInventory(null, Guid.NewGuid(), itemsCatalog);
    Check(drops.TryCollect(firstDrop.EntityId, inventory, out _, now + 300) == 0 &&
        inventory.Held().Count == 0, "Item cannot be picked up before its delay");

    // Fill all regular slots so a mined item cannot simply disappear when
    // the player lacks capacity; then free exactly one item slot.
    for (int slotIndex = 9; slotIndex <= 44; slotIndex++)
        Check(inventory.TryCreativeSet(slotIndex, 1, 64, true),
            "Fill inventory slot to test full-inventory pickup");
    Check(drops.TryCollect(firstDrop.EntityId, inventory, out _, now + 2000) == 0 &&
        drops.Snapshot().Length == 1, "Full inventory leaves the drop on the ground");
    Check(inventory.TryCreativeSet(36, 55, 63, true), "Create one matching stack vacancy");
    Check(drops.TryCollect(firstDrop.EntityId, inventory, out GroundItem leftover, now + 2100) == 1 &&
        leftover.Count == 2 && inventory.Held() == new ItemStack(55, 64),
        "Partial pickup fills inventory and preserves leftover ground items");
    var reloaded = new GroundItemStore(dropsRoot, "test-world", () => ++entitySequence, itemsCatalog);
    Check(reloaded.Snapshot() is [{ ItemId: 55, Count: 2 }],
        "Uncollected items persist across server restarts");
    Check(inventory.TryCreativeSet(9, 0, 0, true), "Free another inventory slot");
    Check(reloaded.TryCollect(reloaded.Snapshot()[0].EntityId, inventory,
            out GroundItem finalStack, now + 2200) == 2 &&
        finalStack.Count == 0 && reloaded.Snapshot().Length == 0,
        "Nearby player can pick up remaining items and remove entity");
    var emptyReload = new GroundItemStore(dropsRoot, "test-world", () => ++entitySequence, itemsCatalog);
    Check(emptyReload.Snapshot().Length == 0,
        "Collected items do not reappear after restart");
}
finally
{
    try { Directory.Delete(dropsRoot, recursive: true); }
    catch (IOException) { }
}

// Per-player position persistence: disconnected and restarting players return
// to the same world coordinate and orientation, rather than world spawn.
string positionsRoot = Path.Combine(Path.GetTempPath(),
    "amethyst-positions-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(positionsRoot);
try
{
    var config = new ServerOptions
    {
        GeneratorVersion = 5,
        Seed = 319471,
        ViewDistance = 2,
        SaveGeneratedChunks = false,
        WorldPath = Path.Combine(positionsRoot, "world"),
        OperatorsFile = Path.Combine(positionsRoot, "ops.yaml"),
        DefaultGameMode = "survival"
    };
    string dataRoot = Path.Combine(positionsRoot, "gameplay");
    Guid userId = Guid.NewGuid(), differentUser = Guid.NewGuid();
    await using (var world = new TerrainWorld(config))
    {
        var store = new PlayerPositionStore(dataRoot, config.WorldPath, world);
        Check(!store.TryLoad(userId, out _),
            "First join has no saved position");
        PlayerPose logoutPosition = new(-35.25, 108.125, 87.875, 133.5f, -27.25f, false);
        PlayerPose walkedTo = new(-36.125, 109.75, 89.25, -91.5f, 22.75f, true);
        Check(store.Valid(logoutPosition), "Saved Creative/Survival coordinates and facing are valid");
        store.Save(userId, logoutPosition);
        Check(store.TryLoad(userId, out PlayerPose restored) && restored == logoutPosition,
            "Position, yaw, pitch and on-ground flag survive an atomic save");
        Check(!store.TryLoad(differentUser, out _),
            "One player's saved coordinates do not affect another player's join");
        var otherWorldStore = new PlayerPositionStore(dataRoot,
            Path.Combine(positionsRoot, "different-world"), world);
        Check(!otherWorldStore.TryLoad(userId, out _),
            "Different world directories do not share player positions");

        using (var wire = new MemoryStream())
        using (var channel = new PacketChannel(wire))
        {
            var game = new MinecraftServer(config, world, null!, world, dataRoot,
                itemsCatalog, recipesCatalog);
            Check(game.TryAdmit(new VerifiedProfile(userId, "PositionTester",
                Array.Empty<ProfileProperty>()), channel, 0, out _),
                "Reconnecting player can be admitted");
            ConnectedPlayer joined = game.GetPlayerForTests(userId)!;
            Check(joined.Pose == logoutPosition,
                "Gameplay restores the saved position before sending Join Game");
            Check(joined.GameMode == PlayerGameMode.Survival,
                "Default gamemode remains independent of saved location");

            // Simulate movement followed by a real ready-player logout.
            joined.MarkReady();
            joined.SetPose(walkedTo);
            await game.OnDisconnectedAsync(userId);
            Check(new PlayerPositionStore(dataRoot, config.WorldPath, world)
                .TryLoad(userId, out PlayerPose loggedOut) && loggedOut == walkedTo,
                "Disconnect saves the player's last server-validated position");

            // Recreate Gameplay as if the process restarted: admission must
            // use the saved coordinates rather than a hard-coded spawn.
            var restarted = new MinecraftServer(config, world, null!, world, dataRoot,
                itemsCatalog, recipesCatalog);
            Check(restarted.TryAdmit(new VerifiedProfile(userId, "PositionTester",
                Array.Empty<ProfileProperty>()), channel, 0, out _),
                "Reconnecting after gameplay restart succeeds");
            Check(restarted.GetPlayerForTests(userId)!.Pose == walkedTo,
                "Reconnecting after restart restores last logout position");
            // Login not completed: should not erase a previously saved position.
            await restarted.OnDisconnectedAsync(userId);
            Check(store.TryLoad(userId, out PlayerPose afterFailedLogin) &&
                afterFailedLogin == walkedTo,
                "Incomplete login does not overwrite saved coordinates");
        }

        // Wire-level verification of the actual initial teleport packet.
        using (var teleportStream = new MemoryStream())
        {
            using var teleportChannel = new PacketChannel(teleportStream);
            await SpawnPackets.SendPositionAsync(teleportChannel, SpawnPackets.TeleportId,
                logoutPosition.X, logoutPosition.Y, logoutPosition.Z,
                logoutPosition.Yaw, logoutPosition.Pitch, CancellationToken.None);
            teleportStream.Position = 0;
            Packet? teleport = await Protocol.ReadPacketAsync(teleportStream, CancellationToken.None);
            Check(teleport is { Id: 0x49 }, "Initial spawn uses protocol 777 position packet");
            if (teleport is { } positionPacket)
            {
                PacketReader p = positionPacket.Reader();
                Check(p.ReadVarInt() == SpawnPackets.TeleportId &&
                    p.ReadDouble() == logoutPosition.X &&
                    p.ReadDouble() == logoutPosition.Y &&
                    p.ReadDouble() == logoutPosition.Z &&
                    p.ReadDouble() == 0 && p.ReadDouble() == 0 && p.ReadDouble() == 0 &&
                    p.ReadFloat() == logoutPosition.Yaw &&
                    p.ReadFloat() == logoutPosition.Pitch &&
                    p.ReadInt32() == 0,
                    "Initial teleport sends restored coordinates, yaw and pitch, not world spawn");
                p.RequireEnd();
            }
        }

        Check(!store.Valid(new PlayerPose(double.NaN, 80, 0, 0, 0, true)) &&
            !store.Valid(new PlayerPose(30_000_000, 80, 0, 0, 0, true)) &&
            !store.Valid(new PlayerPose(0, -1000, 0, 0, 0, true)) &&
            !store.Valid(new PlayerPose(0, 80, 0, 0, 150, true)),
            "Invalid coordinates and view angles are not restorable");
        store.Save(userId, new PlayerPose(double.NaN, 80, 0, 0, 0, true));
        Check(store.TryLoad(userId, out PlayerPose afterRejectedSave) &&
            afterRejectedSave == walkedTo,
            "Invalid position cannot overwrite a valid save");

        // Tamper the on-disk seed: a player should return to the new
        // world's own spawn, not a location from an incompatible world.
        string filePath = Directory.GetFiles(Path.Combine(dataRoot, "positions"),
            "*.bin", SearchOption.AllDirectories).Single();
        byte[] savedBytes = File.ReadAllBytes(filePath);
        byte[] wrongSeed = (byte[])savedBytes.Clone();
        wrongSeed[8] ^= 1;
        File.WriteAllBytes(filePath, wrongSeed);
        Check(!store.TryLoad(userId, out _), "Position file with a different world seed is ignored");
        File.WriteAllBytes(filePath, "corrupted"u8.ToArray());
        Check(!store.TryLoad(userId, out _), "Corrupt position file safely falls back to spawn");

        using var fallbackWire = new MemoryStream();
        using var fallbackChannel = new PacketChannel(fallbackWire);
        var fallbackGame = new MinecraftServer(config, world, null!, world, dataRoot,
                itemsCatalog, recipesCatalog);
        Check(fallbackGame.TryAdmit(new VerifiedProfile(userId, "PositionTester",
                Array.Empty<ProfileProperty>()), fallbackChannel, 0, out _),
            "Damaged save must not interrupt player login");
        PlayerPose fallback = fallbackGame.GetPlayerForTests(userId)!.Pose;
        Check(fallback.X == world.SpawnXPosition &&
            fallback.Y == world.SpawnYPosition && fallback.Z == world.SpawnZPosition,
            "Missing or corrupt saves use the current world spawn");
    }
}
finally
{
    try { Directory.Delete(positionsRoot, recursive: true); }
    catch (IOException) { }
}
Console.WriteLine("PASS: world-scoped player position save, reconnect and initial teleport");

// Workstation UI, recipe and smelting integration: exact protocol-777
// container/menu ID mapping, slot transactions, and persisted furnace stacks.
string stationsRoot = Path.Combine(Path.GetTempPath(),
    "amethyst-stations-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(stationsRoot);
try
{
    var settings = new ServerOptions
    {
        Seed = 91841,
        GeneratorVersion = 5,
        ViewDistance = 2,
        SaveGeneratedChunks = false,
        WorldPath = Path.Combine(stationsRoot, "world")
    };
    await using var stationWorld = new TerrainWorld(settings);
    var stations = new WorkstationManager(stationsRoot, settings.WorldPath, stationWorld, itemsCatalog, recipesCatalog);
    var bag = new PlayerInventory(null, Guid.NewGuid(), itemsCatalog);
    Guid playerId = Guid.NewGuid();
    Check(StationProtocol.MenuType(StationKind.Crafting) == 12 &&
        StationProtocol.MenuType(StationKind.Furnace) == 14 &&
        StationProtocol.MenuType(StationKind.BlastFurnace) == 10 &&
        StationProtocol.MenuType(StationKind.Smoker) == 22,
        "Java 26.3 workstation menu registry types are pinned");
    Check(WorkstationManager.FromBlockState(6866) == StationKind.Crafting &&
        WorkstationManager.FromBlockState(6884) == StationKind.Furnace &&
        WorkstationManager.FromBlockState(24112) == StationKind.Smoker &&
        WorkstationManager.FromBlockState(24120) == StationKind.BlastFurnace,
        "World block states dispatch to the correct workstation");
    Check(CreativeBlockPalette.TryGetState(407, out ushort furnacePlaced) &&
        furnacePlaced == 6884 &&
        CreativeBlockPalette.TryGetState(1507, out ushort smokerPlaced) &&
        smokerPlaced == 24112 &&
        CreativeBlockPalette.TryGetState(1508, out ushort blastPlaced) &&
        blastPlaced == 24120, "Furnace-family blocks are now placeable");
    Check(MiningRules.ForState(6884).DropItem == 407 &&
        MiningRules.ForState(24112).DropItem == 1507 &&
        MiningRules.ForState(24120).DropItem == 1508,
        "Furnace-family blocks return their own items when mined");

    var craftMenu = stations.Open(playerId, new StationPos(1, 81, 1), StationKind.Crafting);
    Check(bag.TryCreativeSet(36, 163, 1, true), "Prepare one oak log for crafting");
    Check(stations.Click(playerId, bag,
        new InventoryClick(craftMenu.WindowId, 0, 37, 0, 0)),
        "Pick up a log from the mapped hotbar in the crafting window");
    Check(stations.Click(playerId, bag,
        new InventoryClick(craftMenu.WindowId, 1, 1, 0, 0)),
        "Deposit one oak log in the crafting table grid");
    var craftPreview = stations.Content(playerId, bag);
    Check(craftPreview?.Slots[0] == new ItemStack(63, 4),
        "Server matches vanilla oak log into four oak planks");
    Check(!stations.Click(playerId, bag,
        new InventoryClick(craftMenu.WindowId, 1, 0, 0, 0)),
        "Stale container revision cannot mint another crafting result");
    Check(stations.Click(playerId, bag,
        new InventoryClick(craftMenu.WindowId, 2, 0, 0, 0)),
        "Taking the crafting result consumes its recipe input");
    Check(stations.Content(playerId, bag)?.Slots[0].Count == 0 &&
        bag.Snapshot().Cursor == new ItemStack(63, 4),
        "Crafting output is delivered once, with input removed from the grid");
    Check(stations.Click(playerId, bag,
        new InventoryClick(craftMenu.WindowId, 3, 37, 0, 0)) &&
        bag.Snapshot().Cursor.Count == 0,
        "Depositing the crafted planks back into the mapped hotbar clears cursor");
    Check(stations.Close(playerId).Length == 0,
        "Empty crafting grid returns no duplicated ingredients");

    // Real shared furnace: shift-click ingredient and fuel into its slots,
    // simulate 10s of server ticks, and persist its output and remaining input.
    var furnacePos = new StationPos(2, 81, 1);
    var furnaceMenu = stations.Open(playerId, furnacePos, StationKind.Furnace);
    Check(bag.TryCreativeSet(36, 1017, 2, true) &&
        bag.TryCreativeSet(37, 1010, 1, true),
        "Prepare two raw iron and one coal in the hotbar");
    Check(stations.Click(playerId, bag,
        new InventoryClick(furnaceMenu.WindowId, 0, 30, 0, 1)),
        "Shift-click raw iron into furnace input");
    Check(stations.Click(playerId, bag,
        new InventoryClick(furnaceMenu.WindowId, 1, 31, 0, 1)),
        "Shift-click coal into furnace fuel slot");
    Check(stations.SnapshotStation(furnacePos)[0] == new ItemStack(1017, 2) &&
        stations.SnapshotStation(furnacePos)[1] == new ItemStack(1010, 1),
        "Furnace inventory is server-owned");
    for (int t = 0; t < 200; t++) stations.Tick();
    var smelted = stations.SnapshotStation(furnacePos);
    Check(smelted[0] == new ItemStack(1017, 1) &&
        smelted[1].Count == 0 && smelted[2] == new ItemStack(1018, 1),
        "A furnace consumes fuel/input and smelts raw iron into an ingot");
    Check(stations.Data(playerId).Burn > 0,
        "Furnace progress properties expose remaining burn time");
    stations.Save();
    var restoredStations = new WorkstationManager(stationsRoot, settings.WorldPath, stationWorld, itemsCatalog, recipesCatalog);
    Check(restoredStations.SnapshotStation(furnacePos).SequenceEqual(smelted),
        "Furnace contents survive a world restart");
    Check(!stations.Click(playerId, bag,
        new InventoryClick(furnaceMenu.WindowId, 0, 2, 0, 0)),
        "A stale furnace click cannot take output");
    // The next valid revision succeeds. It must consume output only once.
    Check(stations.Click(playerId, bag,
        new InventoryClick(furnaceMenu.WindowId, furnaceMenu.Revision, 2, 0, 0)),
        "Taking furnace output through the current authoritative revision");
    Check(stations.SnapshotStation(furnacePos)[2].Count == 0 &&
        bag.Snapshot().Cursor == new ItemStack(1018, 1),
        "Smelted output reaches cursor once and is removed from station");
    Check(stations.Close(playerId).Length == 0,
        "Closing furnace leaves shared block inventory in place");

    foreach (var (kind, rawId, resultId, pos) in new[]
    {
        (StationKind.Smoker, 1260, 1261, new StationPos(3, 81, 1)),
        (StationKind.BlastFurnace, 1017, 1018, new StationPos(4, 81, 1))
    })
    {
        Check(recipesCatalog.TryGetCookingRecipe(kind == StationKind.Smoker
                ? ShardCookingStation.Smoker : ShardCookingStation.BlastFurnace,
                rawId, out ShardCookingMatch cooked) &&
            cooked.Result == new ShardRecipeStack(resultId, 1) &&
            cooked.CookingTicks == 100,
            "Smoker and blast furnace enforce their accelerated recipe categories");
        var menu = stations.Open(playerId, pos, kind);
        // Direct block-state seeding for the persistent cooking simulation.
        Check(bag.TryCreativeSet(36, rawId, 1, true) &&
            bag.TryCreativeSet(37, 1010, 1, true),
            "Prepare quick-cooking station input and fuel");
        Check(stations.Click(playerId, bag,
            new InventoryClick(menu.WindowId, 0, 30, 0, 1)) &&
            stations.Click(playerId, bag,
            new InventoryClick(menu.WindowId, 1, 31, 0, 1)),
            "Shift-click both ingredient and coal into workstation");
        for (int t = 0; t < 100; t++) stations.Tick();
        Check(stations.SnapshotStation(pos)[2] == new ItemStack(resultId, 1),
            "Fast cooking produces a real output stack in its workstation");
        stations.Close(playerId);
    }
    Check(!recipesCatalog.TryGetCookingRecipe(ShardCookingStation.Smoker, 1017, out _) &&
        !recipesCatalog.TryGetCookingRecipe(ShardCookingStation.BlastFurnace, 1260, out _),
        "Smoker rejects ore and blast furnace rejects food");

    var (released, _) = stations.Break(furnacePos);
    Check(released.Length > 0 && released.All(s => s.ItemId is 1017 or 1018) &&
        released.Sum(s => s.Count) == 1,
        "Breaking a furnace releases all remaining input/output instead of deleting it");
    stations.Save();
    Check(new WorkstationManager(stationsRoot, settings.WorldPath, stationWorld, itemsCatalog, recipesCatalog)
        .SnapshotStation(furnacePos).Length == 0,
        "Destroyed furnace does not reappear in saved workstation storage");

    // Exercise the actual Gameplay Shard use-on-block path in both modes.
    // A station should open its GUI instead of trying to place the held item.
    using (var gameplayOutput = new MemoryStream())
    using (var gameplayChannel = new PacketChannel(gameplayOutput))
    {
        var stationPlayer = new ConnectedPlayer(Guid.NewGuid(), "StationTester",
            Array.Empty<ProfileProperty>(), 77, gameplayChannel,
            stationWorld, null, itemsCatalog);
        stationPlayer.MarkReady();
        var gameplay = new MinecraftServer(settings, stationWorld, null!,
            stationWorld, Path.Combine(stationsRoot, "gameplay-integration"),
            itemsCatalog, recipesCatalog);
        int seq = 1;
        foreach (var (kind, mode, state, blockPosition) in new[]
        {
            (StationKind.Crafting, PlayerGameMode.Creative, (ushort)6866, new StationPos(1, 88, 1)),
            (StationKind.Furnace, PlayerGameMode.Survival, (ushort)6884, new StationPos(2, 88, 1)),
            (StationKind.Smoker, PlayerGameMode.Creative, (ushort)24112, new StationPos(3, 88, 1)),
            (StationKind.BlastFurnace, PlayerGameMode.Survival, (ushort)24120, new StationPos(4, 88, 1))
        })
        {
            Check(stationWorld.TrySetBlockState(blockPosition.X, blockPosition.Y,
                blockPosition.Z, state, out _), "Install workstation block in test world");
            stationPlayer.SetGameMode(mode);
            stationPlayer.SetPose(new PlayerPose(blockPosition.X + 0.5,
                blockPosition.Y + 1, blockPosition.Z + 0.5, 0, 0, true));
            await gameplay.HandleBlockUseAsync(stationPlayer,
                new BlockUseOn(0, blockPosition.X, blockPosition.Y, blockPosition.Z,
                    1, seq++, true), CancellationToken.None);
            Check(gameplay.StationsForTests.GetMenu(stationPlayer.PlayerUuid) is
                { Kind: var openedKind, Position: var openedPos } &&
                openedKind == kind && openedPos == blockPosition,
                $"Gameplay opens {kind} menu in {mode}");
            Check(stationWorld.GetBlockState(blockPosition.X,
                blockPosition.Y, blockPosition.Z) == state,
                "Opening station does not accidentally replace its world block");
        }
        gameplayOutput.Position = 0;
        Packet? firstStationPacket = await Protocol.ReadPacketAsync(gameplayOutput, CancellationToken.None);
        Check(firstStationPacket is { Id: StationProtocol.OpenScreen },
            "Real server use-on-block sends the Open Screen packet first");
    }

    using (var stream = new MemoryStream())
    {
        using var wire = new PacketChannel(stream);
        await StationProtocol.OpenAsync(wire, 1,
            StationProtocol.FurnaceMenu, "Furnace", CancellationToken.None);
        await StationProtocol.SendContentAsync(wire, 1, 0,
            new ItemStack[39], ItemStack.Empty, CancellationToken.None);
        await StationProtocol.SendDataAsync(wire, 1, 0, 1600, CancellationToken.None);
        stream.Position = 0;
        Packet? opened = await Protocol.ReadPacketAsync(stream, CancellationToken.None);
        Check(opened is { Id: StationProtocol.OpenScreen } &&
            opened.Value.Reader().ReadVarInt() == 1,
            "Opening a workstation sends clientbound open_screen 0x3C");
        Packet? contents = await Protocol.ReadPacketAsync(stream, CancellationToken.None);
        Check(contents is { Id: InventoryProtocol.ClientboundSetContent },
            "New workstation sends clientbound full window contents");
        Packet? data = await Protocol.ReadPacketAsync(stream, CancellationToken.None);
        Check(data is { Id: StationProtocol.ContainerSetData },
            "Furnace burning and cooking progress use container_set_data 0x13");
    }
}
finally
{
    try { Directory.Delete(stationsRoot, recursive: true); }
    catch (IOException) { }
}
Console.WriteLine("PASS: crafting table, furnace, smoker, blast furnace and persistent workstation menus");

Console.WriteLine("PASS: dropped items, item-entity packets, gravity, pickup and persistence");
Console.WriteLine("PASS: block codec, modes, item stacks, timed mining, edit overlay and persistence");

sealed class TestFlatBlockWorld : Amethyst.Shards.IShardBlockWorld
{
    public ushort GetBlockState(int x, int y, int z) => y <= 63 ? (ushort)1 : (ushort)0;
    public bool TrySetBlockState(int x, int y, int z, ushort state, out ushort previous)
    {
        previous = GetBlockState(x, y, z);
        return false;
    }
}

