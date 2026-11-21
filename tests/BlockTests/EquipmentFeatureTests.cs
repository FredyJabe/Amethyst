using Amethyst;
using Amethyst.BuiltinShards;
using Amethyst.Shards;

internal static class EquipmentFeatureTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static async Task RunAsync(ItemCatalog items, RecipeCatalog recipes)
    {
        if (!items.TryGetByIdentifier("minecraft:diamond_sword", out var sword) ||
            !items.TryGetByIdentifier("minecraft:shield", out var shield) ||
            !items.TryGetByIdentifier("minecraft:diamond_helmet", out var helmet))
            throw new InvalidDataException("Missing equipment definitions.");
        string root = Path.Combine(Path.GetTempPath(),
            "amethyst-equipment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new ServerOptions
            {
                Seed = 7766, GeneratorVersion = 5, ViewDistance = 2,
                SaveGeneratedChunks = false,
                WorldPath = Path.Combine(root, "world")
            };
            await using var world = new TerrainWorld(settings);
            var engine = new SkillEngine();
            engine.RegisterTrigger("equipment-tests", "onSwap");
            engine.RegisterTrigger("equipment-tests", "onEquip");
            engine.RegisterTrigger("equipment-tests", "onUnequip");
            var events = new List<string>();
            bool cancelSwap = false;
            engine.RegisterMechanic("equipment-tests", "record",
                skill => { },
                (skill, evt, ct) =>
                {
                    events.Add(skill.Arguments["event"]);
                    return ValueTask.CompletedTask;
                });
            engine.RegisterMechanic("equipment-tests", "testCancel",
                skill => { },
                (skill, evt, ct) =>
                {
                    if (cancelSwap) evt.Runtime.Cancel();
                    return ValueTask.CompletedTask;
                });
            engine.RegisterInstructions("equipment-tests", [
                SkillEngine.Parse("minecraft:diamond_sword",
                    "record{event=sword_swapped} @self ~onSwap"),
                SkillEngine.Parse("minecraft:shield",
                    "testCancel @self ~onSwap"),
                SkillEngine.Parse("minecraft:shield",
                    "record{event=shield_swapped} @self ~onSwap"),
                SkillEngine.Parse("minecraft:shield",
                    "record{event=shield_unequipped} @self ~onUnequip"),
                SkillEngine.Parse("minecraft:diamond_sword",
                    "record{event=sword_equipped} @self ~onEquip"),
                SkillEngine.Parse("minecraft:diamond_helmet",
                    "record{event=helmet_equipped} @self ~onEquip")
            ]);

            using var stream = new MemoryStream();
            using var channel = new PacketChannel(stream);
            var server = TestServerFactory.Create(settings, world,
                null!, world, inventoryDirectory: root,
                items: items, recipes: recipes, skills: engine);
            Guid playerId = Guid.NewGuid();
            Check(server.TryAdmit(new VerifiedProfile(
                    playerId, "EquipmentTester", []), channel, 0, out _),
                "Admit a player for equipment and offhand transactions");
            ConnectedPlayer player = server.GetPlayerForTests(playerId)!;
            player.MarkReady();
            player.SetGameMode(PlayerGameMode.Survival);
            using var observerStream = new MemoryStream();
            using var observerChannel = new PacketChannel(observerStream);
            Guid observerId = Guid.NewGuid();
            Check(server.TryAdmit(new VerifiedProfile(
                    observerId, "EquipmentViewer", []), observerChannel, 0, out _),
                "Admit a separate viewer for equipment packet replication");
            server.GetPlayerForTests(observerId)!.MarkReady();
            Check(player.Inventory.TryCreativeSet(36, sword.ProtocolId, 1, true) &&
                player.Inventory.TryCreativeSet(45, shield.ProtocolId, 1, true),
                "Set up a sword and shield in separate equipped hands");
            using (var protocolStream = new MemoryStream())
            using (var protocolChannel = new PacketChannel(protocolStream))
            {
                EntityEquipment[] visible = EquipmentProtocol.AllSlots(player.Inventory);
                Check(visible.Length == 2 && visible[0].Slot == 0 &&
                    visible[1].Slot == 1,
                    "Initial equipment snapshot includes main hand and offhand");
                await EquipmentProtocol.SendAsync(protocolChannel,
                    player.EntityId, visible, items, CancellationToken.None);
                protocolStream.Position = 0;
                Packet? packet = await Protocol.ReadPacketAsync(
                    protocolStream, CancellationToken.None);
                if (packet is not { Id: FlightProtocol.ClientboundSetEquipment })
                    throw new Exception("FAIL: Missing Java 26.3 equipment packet.");
                var equipment = packet.Value.Reader();
                Check(equipment.ReadVarInt() == player.EntityId &&
                    equipment.ReadByte() == 0x80 &&
                    equipment.ReadVarInt() == 1 &&
                    equipment.ReadVarInt() == sword.ProtocolId &&
                    equipment.ReadVarInt() == 0 &&
                    equipment.ReadVarInt() == 0 &&
                    equipment.ReadByte() == 1 &&
                    equipment.ReadVarInt() == 1 &&
                    equipment.ReadVarInt() == shield.ProtocolId &&
                    equipment.ReadVarInt() == 0 &&
                    equipment.ReadVarInt() == 0 &&
                    equipment.Remaining == 0,
                    "Equipment packet uses continuation bit and valid item-stack wire fields");
            }
            long observerBeforeSwap = observerStream.Length;
            int revision = player.Inventory.Snapshot().Revision;
            await server.HandleBlockActionAsync(player,
                new PlayerBlockAction(BlockInteractionProtocol.SwapItemWithOffhand,
                    0, 0, 0, 0, 81), CancellationToken.None);
            var current = player.Inventory.Snapshot();
            Check(current.Slots[36].ItemId == shield.ProtocolId &&
                current.Slots[45].ItemId == sword.ProtocolId &&
                current.Revision == revision + 1 &&
                events.Contains("sword_swapped") &&
                events.Contains("shield_swapped") &&
                events.Contains("shield_unequipped") &&
                events.Contains("sword_equipped"),
                "F-key swaps both component-preserving stacks and emits item skill events");
            using (var latest = new MemoryStream(observerStream.ToArray()
                .AsSpan((int)observerBeforeSwap).ToArray()))
            {
                Packet? broadcast = await Protocol.ReadPacketAsync(
                    latest, CancellationToken.None);
                Check(broadcast is { Id: FlightProtocol.ClientboundSetEquipment },
                    "Accepted F-key swap replicates equipment to other players");
            }
            cancelSwap = true;
            int beforeCancelled = events.Count;
            await server.HandleBlockActionAsync(player,
                new PlayerBlockAction(BlockInteractionProtocol.SwapItemWithOffhand,
                    0, 0, 0, 0, 82), CancellationToken.None);
            var unchanged = player.Inventory.Snapshot();
            Check(unchanged.Revision == current.Revision &&
                unchanged.Slots[36] == current.Slots[36] &&
                unchanged.Slots[45] == current.Slots[45] &&
                events.Count == beforeCancelled + 2,
                "onSwap cancelEvent suppresses transaction but runs remaining skills");

            var beforeHelmet = player.Inventory.Snapshot().Slots;
            long observerBeforeHelmet = observerStream.Length;
            Check(player.Inventory.TryCreativeSet(5, helmet.ProtocolId, 1, true),
                "An authoritative accepted creative slot equips a helmet");
            await server.NotifyEquipmentChangesAsync(
                player, beforeHelmet, CancellationToken.None);
            Check(events.Contains("helmet_equipped") &&
                new PlayerInventory(root, playerId, items).Snapshot().Slots[45]
                    .ItemId == sword.ProtocolId,
                "Equipment notifications follow committed slots and offhand changes persist");
            Check(observerStream.Length > observerBeforeHelmet,
                "Equipping armor sends a visible head-slot update to observers");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
