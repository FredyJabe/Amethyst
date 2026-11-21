using Amethyst;
using Amethyst.BuiltinShards;
using Amethyst.Shards;

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
            bool cancelRightClick = false;
            bool cancelShear = false, cancelMilk = false;
            int rightClickRuns = 0, shearRuns = 0, milkRuns = 0;
            var interactionSkills = new SkillEngine();
            interactionSkills.RegisterTrigger("interact-tests", "onRightClick");
            interactionSkills.RegisterTrigger("interact-tests", "onShear");
            interactionSkills.RegisterTrigger("interact-tests", "onMilk");
            interactionSkills.RegisterMechanic("interact-tests", "testCancel",
                skill => { },
                (skill, evt, ct) =>
                {
                    if (cancelRightClick) evt.Runtime.Cancel();
                    return ValueTask.CompletedTask;
                });
            interactionSkills.RegisterMechanic("interact-tests", "testRecord",
                skill => { },
                (skill, evt, ct) =>
                {
                    rightClickRuns++;
                    return ValueTask.CompletedTask;
                });
            interactionSkills.RegisterMechanic("interact-tests", "testEntityCancel",
                skill => { },
                (skill, evt, ct) =>
                {
                    if (evt.Trigger == "onShear" && cancelShear ||
                        evt.Trigger == "onMilk" && cancelMilk)
                        evt.Runtime.Cancel();
                    return ValueTask.CompletedTask;
                });
            interactionSkills.RegisterMechanic("interact-tests", "testEntityRecord",
                skill => { },
                (skill, evt, ct) =>
                {
                    if (evt.Trigger == "onShear") shearRuns++;
                    if (evt.Trigger == "onMilk") milkRuns++;
                    return ValueTask.CompletedTask;
                });
            interactionSkills.RegisterInstructions("interact-tests", [
                SkillEngine.Parse("minecraft:shears",
                    "testCancel @self ~onRightClick"),
                SkillEngine.Parse("minecraft:shears",
                    "testRecord @self ~onRightClick")
            ]);
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null &&
                !File.Exists(Path.Combine(repository.FullName, "Amethyst.slnx")))
                repository = repository.Parent;
            if (repository is null)
                throw new InvalidOperationException("Missing repository root.");
            var mobCatalog = new EntityCatalog(Path.Combine(repository.FullName,
                "shards", "Amethyst.Shards.Entities", "entities-26.3.yml"));
            var mobManager = new EntityManager(mobCatalog);
            var server = TestServerFactory.Create(settings, world,
                null!, world, items: items, recipes: recipes,
                entities: mobManager, entityTypes: mobCatalog,
                interactions: mobManager, skills: interactionSkills);
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

            actor.SetGameMode(PlayerGameMode.Survival);
            Check(server.SummonSheepAt(actor),
                "Prepare an extra sheep to test cancellable entity interactions");
            LivingEntity toCancel = server.LivingEntitiesForTests.Last();
            int dropsBeforeCancel = server.GroundItemsForTests.Length;
            int wearBeforeCancel = actor.Inventory.Held().Damage;
            cancelRightClick = true;
            Check(await server.HandleValidatedEntityInteractionAsync(actor,
                    new EntityInteraction(toCancel.EntityId, 0, false),
                    CancellationToken.None) &&
                !server.LivingEntitiesForTests.Single(x =>
                    x.EntityId == toCancel.EntityId).Sheared &&
                actor.Inventory.Held().Damage == wearBeforeCancel &&
                server.GroundItemsForTests.Length == dropsBeforeCancel &&
                rightClickRuns == 1,
                "onRightClick cancellation prevents shearing but executes later item skills once");
            cancelRightClick = false;
            Check(await server.HandleValidatedEntityInteractionAsync(actor,
                    new EntityInteraction(toCancel.EntityId, 0, false),
                    CancellationToken.None) &&
                server.LivingEntitiesForTests.Single(x =>
                    x.EntityId == toCancel.EntityId).Sheared &&
                actor.Inventory.Held().Damage == wearBeforeCancel + 1 &&
                rightClickRuns == 2,
                "Accepted right-click shears the authoritative sheep and dispatches once");
            Check(!await server.HandleValidatedEntityInteractionAsync(actor,
                    new EntityInteraction(int.MaxValue, 0, false),
                    CancellationToken.None) && rightClickRuns == 2,
                "Forged entity IDs cannot trigger item skills or spawn loot");

            interactionSkills.RegisterInstructions("entity-event-tests", [
                SkillEngine.Parse("minecraft:sheep",
                    "testEntityCancel @self ~onShear"),
                SkillEngine.Parse("minecraft:sheep",
                    "testEntityRecord @self ~onShear"),
                SkillEngine.Parse("minecraft:cow",
                    "testEntityCancel @self ~onMilk"),
                SkillEngine.Parse("minecraft:cow",
                    "testEntityRecord @self ~onMilk")
            ]);
            Check(server.SummonSheepAt(actor),
                "Spawn a fresh sheep for pre-commit onShear cancellation");
            LivingEntity conditionalSheep = server.LivingEntitiesForTests.Last();
            int wearBeforeShear = actor.Inventory.Held().Damage;
            cancelShear = true;
            Check(await server.HandleValidatedEntityInteractionAsync(actor,
                    new EntityInteraction(conditionalSheep.EntityId, 0, false),
                    CancellationToken.None) &&
                !server.LivingEntitiesForTests.Single(e =>
                    e.EntityId == conditionalSheep.EntityId).Sheared &&
                actor.Inventory.Held().Damage == wearBeforeShear && shearRuns == 1,
                "onShear cancellation prevents wool removal and wear while executing later skills");
            cancelShear = false;
            Check(await server.HandleValidatedEntityInteractionAsync(actor,
                    new EntityInteraction(conditionalSheep.EntityId, 0, false),
                    CancellationToken.None) &&
                server.LivingEntitiesForTests.Single(e =>
                    e.EntityId == conditionalSheep.EntityId).Sheared &&
                actor.Inventory.Held().Damage == wearBeforeShear + 1 &&
                shearRuns == 2,
                "Accepted ~onShear occurs exactly once before the sheep changes");

            ShardItemDefinition emptyBucket = default;
            ShardItemDefinition filledBucket = default;
            ShardEntityInstance milkingCow = default;
            Check(items.TryGetByIdentifier("minecraft:bucket", out emptyBucket) &&
                items.TryGetByIdentifier("minecraft:milk_bucket", out filledBucket) &&
                mobManager.TrySpawn("cow", 2, 81, 1,
                    out milkingCow) &&
                actor.Inventory.TryCreativeSet(
                    36, emptyBucket.ProtocolId, 1, true),
                "Prepare a generic cow for cancellable main-hand milking");
            cancelMilk = true;
            Check(await server.HandleValidatedEntityInteractionAsync(actor,
                    new EntityInteraction(milkingCow.EntityId, 0, false),
                    CancellationToken.None) &&
                actor.Inventory.Held().ItemId == emptyBucket.ProtocolId &&
                milkRuns == 1,
                "onMilk cancelEvent prevents bucket conversion but runs later skills");
            cancelMilk = false;
            Check(await server.HandleValidatedEntityInteractionAsync(actor,
                    new EntityInteraction(milkingCow.EntityId, 0, false),
                    CancellationToken.None) &&
                actor.Inventory.Held().ItemId == filledBucket.ProtocolId &&
                milkRuns == 2,
                "Successful milking converts the validated bucket once");
            Check(mobManager.TrySpawn("cow", 2, 81, 1,
                    out var offhandCow) &&
                actor.Inventory.TryCreativeSet(45, emptyBucket.ProtocolId, 1, true),
                "Prepare offhand bucket milking");
            Check(await server.HandleValidatedEntityInteractionAsync(actor,
                    new EntityInteraction(offhandCow.EntityId, 1, false),
                    CancellationToken.None) &&
                actor.Inventory.Snapshot().Slots[45].ItemId == filledBucket.ProtocolId &&
                milkRuns == 3,
                "Offhand milk conversion preserves main hand and emits the entity skill");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
