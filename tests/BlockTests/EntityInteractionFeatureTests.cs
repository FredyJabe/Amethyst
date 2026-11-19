using Amethyst;
using Amethyst.BuiltinShards;

internal static class EntityInteractionFeatureTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    public static async Task RunAsync(ItemCatalog items, RecipeCatalog recipes)
    {
        var writer = new PacketWriter();
        writer.WriteVarInt(108); writer.WriteVarInt(1);
        writer.WriteByte(0); // LP_VEC3 zero
        writer.WriteBool(false);
        EntityInteraction action = EntityInteractionProtocol.Read(
            new Packet(EntityInteractionProtocol.ServerboundInteract,
                writer.ToArray()));
        Check(action.EntityId == 108 && action.Hand == 1 &&
            !action.SecondaryAction,
            "Entity interaction decoder handles Java 26.3 compressed hit vector");
        Check(items.TryGetByIdentifier("shears", out var shears) &&
            items.TryGetByIdentifier("white_wool", out _),
            "Shears and wool are registered vanilla item identities");

        string root = Path.Combine(Path.GetTempPath(),
            "amethyst-shearing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int nextId = 10000;
            string worldPath = Path.Combine(root, "world");
            var state = new LivingEntityStore(root, worldPath,
                () => ++nextId);
            Check(state.TrySpawnSheep(0.5, 81, 0.5,
                out LivingEntity sheep, color: 14) &&
                state.TryShear(sheep.EntityId, out var sheared) &&
                sheared.Color == 14 && sheared.Sheared &&
                !state.TryShear(sheep.EntityId, out _),
                "Sheep can be sheared once until wool regrows");
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Check(state.Regrow(now, 180_000).Length == 0 &&
                state.Regrow(now + 190_000, 180_000).Length == 1 &&
                !state.Snapshot().Single().Sheared,
                "Wool regrows after a real-world delay and keeps sheep color");
            var restarted = new LivingEntityStore(root, worldPath,
                () => ++nextId);
            Check(!restarted.Snapshot().Single().Sheared &&
                restarted.Snapshot().Single().Color == 14,
                "Regrowth and dyed sheep state persist on restart");

            var settings = new ServerOptions
            {
                Seed = 88470, GeneratorVersion = 5,
                SaveGeneratedChunks = false, ViewDistance = 2,
                WorldPath = Path.Combine(root, "game")
            };
            await using var world = new TerrainWorld(settings);
            using var stream = new MemoryStream();
            using var channel = new PacketChannel(stream);
            var server = new MinecraftServer(settings, world,
                null!, world, items: items, recipes: recipes);
            Guid id = Guid.NewGuid();
            Check(server.TryAdmit(new VerifiedProfile(id, "SheepTester", []),
                channel, 0, out _), "Player can join sheep interaction test");
            ConnectedPlayer actor = server.GetPlayerForTests(id)!;
            actor.MarkReady();
            actor.SetGameMode(PlayerGameMode.Survival);
            actor.SetPose(new PlayerPose(0.5, 81, 0.5, 0, 0, true));
            Check(server.SummonSheepAt(actor) &&
                actor.Inventory.TryCreativeSet(36, shears.ProtocolId, 1, true),
                "Prepare shears and spawned sheep near the player");
            LivingEntity mob = server.LivingEntitiesForTests.Single();
            Check(await server.HandleEntityInteractAsync(actor,
                    new EntityInteraction(mob.EntityId, 0, false),
                    CancellationToken.None) &&
                actor.Inventory.Held().Damage == 1 &&
                server.LivingEntitiesForTests.Single().Sheared &&
                server.GroundItemsForTests.Any(x => x.Count is >= 1 and <= 3),
                "Survival shearing drops 1–3 wool and damages shears once");
            Check(!await server.HandleEntityInteractAsync(actor,
                    new EntityInteraction(mob.EntityId, 0, false),
                    CancellationToken.None) &&
                actor.Inventory.Held().Damage == 1,
                "Repeating shearing on a sheep with no wool is ignored");
            Check(server.SummonSheepAt(actor),
                "Spawn separate sheep to verify creative shearing");
            actor.SetGameMode(PlayerGameMode.Creative);
            LivingEntity second = server.LivingEntitiesForTests.Last();
            Check(await server.HandleEntityInteractAsync(actor,
                    new EntityInteraction(second.EntityId, 0, false),
                    CancellationToken.None) &&
                actor.Inventory.Held().Damage == 1,
                "Creative shearing transforms sheep without consuming durability");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
