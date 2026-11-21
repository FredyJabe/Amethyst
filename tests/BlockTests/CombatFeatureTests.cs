using Amethyst;
using Amethyst.BuiltinShards;
using Amethyst.Shards;

internal static class CombatFeatureTests
{
    private static void Check(bool ok, string description)
    {
        if (!ok) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    public static async Task RunAsync(ItemCatalog items, RecipeCatalog recipes)
    {
        var hit = new PlayerVitals(null, Guid.NewGuid());
        Check(hit.Snapshot().Health == 20 &&
            hit.TryDamage(5, 1000, out bool killed) && !killed &&
            hit.Snapshot().Health == 15 &&
            !hit.TryDamage(5, 1100, out _) &&
            hit.TryDamage(15, 1600, out killed) && killed &&
            hit.Snapshot().Health == 0, "Player damage uses invulnerability frames and death");
        hit.Respawn();
        Check(hit.Snapshot().Health == 20,
            "Respawn restores full player health");

        PlayerPose a = new(1, 80, 1, 0, 0, true);
        PlayerPose b = new(3, 80, 1, 0, 0, true);
        Check(CombatRules.InReach(a, b) &&
            !CombatRules.InReach(a, b with { X = 8 }) &&
            CombatRules.Mitigate(8, 20) < 8 &&
            CombatRules.MeleeDamage("minecraft:diamond_sword") == 7,
            "Combat uses reach, armor mitigation, and weapon damage rules");
        var meleeEffects = new PlayerActiveEffects();
        Check(meleeEffects.Apply("minecraft:strength", 200, 1) &&
            CombatRules.MeleeDamageWithEffects(
                "minecraft:diamond_sword", meleeEffects.Snapshot()) == 13f &&
            meleeEffects.Apply("minecraft:weakness", 200, 0) &&
            CombatRules.MeleeDamageWithEffects(
                "minecraft:diamond_sword", meleeEffects.Snapshot()) == 9f,
            "Strength II adds six and Weakness I subtracts four melee damage");

        var parserWriter = new PacketWriter();
        parserWriter.WriteVarInt(42);
        Check(CombatProtocol.ReadAttack(new Packet(
            CombatProtocol.ServerboundAttack,
            parserWriter.ToArray())) == 42,
            "Protocol 777 attack payload accepts precisely one entity ID");
        CombatProtocol.ReadPunch(new Packet(CombatProtocol.ServerboundPunch,
            ReadOnlyMemory<byte>.Empty));
        bool malformedPunch = false;
        try
        {
            CombatProtocol.ReadPunch(new Packet(CombatProtocol.ServerboundPunch,
                new byte[] { 1 }));
        }
        catch (InvalidDataException) { malformedPunch = true; }
        Check(malformedPunch, "Protocol 777 punch packet requires an empty body");

        string root = Path.Combine(Path.GetTempPath(),
            "amethyst-combat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Guid uuid = Guid.NewGuid();
            var vital = new PlayerVitals(root, uuid);
            Check(vital.TryDamage(6, Environment.TickCount64,
                out _) && new PlayerVitals(root, uuid).Snapshot().Health == 14,
                "Player health persists across reconnects");
            int next = 10000;
            string worldId = Path.Combine(root, "world");
            var mobs = new LivingEntityStore(root, worldId, () => ++next);
            Check(mobs.TrySpawnSheep(2, 81, 2, out LivingEntity sheep) &&
                sheep.Health == 8 && !sheep.Sheared &&
                mobs.TryShear(sheep.EntityId, out var sheared) &&
                sheared.Sheared,
                "Server creates and shears an authoritative sheep");
            var reloaded = new LivingEntityStore(root, worldId, () => ++next);
            Check(reloaded.Snapshot().Single().Uuid == sheep.Uuid &&
                reloaded.Snapshot().Single().Sheared &&
                reloaded.TryDamage(reloaded.Snapshot().Single().EntityId, 8,
                    out _, out bool dead) && dead &&
                reloaded.Snapshot().Length == 0,
                "Sheared and damaged livestock persist and die correctly");

            var settings = new ServerOptions
            {
                Seed = 829173, GeneratorVersion = 5,
                ViewDistance = 2, SaveGeneratedChunks = false,
                WorldPath = worldId
            };
            await using var world = new TerrainWorld(settings);
            using var stream1 = new MemoryStream();
            using var stream2 = new MemoryStream();
            using var connection1 = new PacketChannel(stream1);
            using var connection2 = new PacketChannel(stream2);
            // The attack event is cancelled by a configured enchantment
            // without changing the semantics of ordinary unenchanted hits.
            DirectoryInfo? solutionRoot = new(AppContext.BaseDirectory);
            while (solutionRoot is not null &&
                !File.Exists(Path.Combine(solutionRoot.FullName, "Amethyst.slnx")))
                solutionRoot = solutionRoot.Parent;
            if (solutionRoot is null)
                throw new InvalidOperationException("Amethyst.slnx not found.");
            string healthDir = Path.Combine(root, "entity-definitions");
            Directory.CreateDirectory(healthDir);
            File.WriteAllText(Path.Combine(healthDir, "health.yml"),
                "cow:\n  Health: 4\nzombie:\n  Health: 4\n");
            var mobTypes = new EntityCatalog(Path.Combine(
                solutionRoot.FullName, "shards", "Amethyst.Shards.Entities",
                "entities-26.3.yml"), healthDir);
            var managedMobs = new EntityManager(mobTypes);
            var enchantments = new EnchantmentCatalog(Path.Combine(
                solutionRoot.FullName, "shards", "Amethyst.Shards.Items",
                "enchantments-26.3.yml"));
            var skills = new SkillEngine();
            var skillContext = new LootTestContext(skills);
            EnchantmentSkills.Register(skillContext);
            LootSkills.Register(skillContext, items);
            var loot = new LootCatalog(Path.Combine(solutionRoot.FullName,
                "shards", "Amethyst.Shards.Items", "loot-26.3.yml"));
            skills.RegisterInstructions("combat-tests", loot.Instructions.Concat([
                SkillEngine.Parse("minecraft:sharpness",
                    "cancelEvent @self ~onAttack"),
                SkillEngine.Parse("minecraft:diamond_sword",
                    "cancelEvent @self ~onBlockDrop"),
                SkillEngine.Parse("minecraft:diamond_sword",
                    "dropItem{item=minecraft:diamond;amount=2} @self ~onBlockDrop"),
                SkillEngine.Parse("minecraft:cow",
                    "dropItem{item=minecraft:beef;amount=1} @self ~onDeath"),
                SkillEngine.Parse("minecraft:zombie",
                    "dropItem{item=minecraft:rotten_flesh;amount=1} @self ~onDeath")
            ]));
            if (!items.TryGetByIdentifier("minecraft:diamond", out var nativeReward))
                throw new InvalidDataException("Expected vanilla diamond reward.");
            var nativeMobLoot = new RecordedVanillaMobLoot(nativeReward.ProtocolId);
            var server = TestServerFactory.Create(settings, world,
                null!, world, items: items, recipes: recipes,
                entities: managedMobs, entityTypes: mobTypes,
                enchantments: enchantments, skills: skills, loot: loot,
                vanillaLoot: nativeMobLoot);
            Guid id1 = Guid.NewGuid(), id2 = Guid.NewGuid();
            Check(server.TryAdmit(new VerifiedProfile(id1, "FighterOne", []),
                connection1, 0, out _) &&
                server.TryAdmit(new VerifiedProfile(id2, "FighterTwo", []),
                    connection2, 0, out _),
                "Combat server admits two independent players");
            ConnectedPlayer player1 = server.GetPlayerForTests(id1)!;
            ConnectedPlayer player2 = server.GetPlayerForTests(id2)!;
            player1.MarkReady(); player2.MarkReady();
            player1.SetGameMode(PlayerGameMode.Survival);
            player2.SetGameMode(PlayerGameMode.Survival);
            player1.SetPose(new PlayerPose(0, 80, 0, 0, 0, true));
            player2.SetPose(new PlayerPose(2, 80, 0, 0, 0, true));
            Check(items.TryGetByIdentifier("diamond_sword", out var sword) &&
                player1.Inventory.TryCreativeSet(36, sword.ProtocolId, 1, true),
                "Prepare a legitimate sword for PvP");
            await server.HandleAttackAsync(player1, player2.EntityId,
                CancellationToken.None);
            Check(player2.Vitals.Snapshot().Health < 20 &&
                player1.Inventory.Held().Damage == 1,
                "A successful PvP attack reduces health and sword durability");

            long observerBeforeSwing = stream2.Length;
            long attackerBeforeSwing = stream1.Length;
            await server.HandlePunchAsync(player1, CancellationToken.None);
            Check(stream2.Length > observerBeforeSwing &&
                stream1.Length == attackerBeforeSwing,
                "Uncancelled punch broadcasts a swing to observers, not attacker");
            using (var outbound = new MemoryStream(stream2.ToArray()
                .AsSpan((int)observerBeforeSwing).ToArray()))
            {
                Packet? swing = await Protocol.ReadPacketAsync(
                    outbound, CancellationToken.None);
                var fields = swing?.Reader() ?? throw new Exception(
                    "Missing swing animation frame");
                Check(swing.Value.Id == CombatProtocol.ClientboundSwingAnimation &&
                    fields.ReadVarInt() == player1.EntityId &&
                    fields.ReadVarInt() == 0 && // main hand
                    fields.ReadVarInt() == 1 && // whack
                    fields.ReadVarInt() == 6 && // duration in ticks
                    fields.Remaining == 0,
                    "Swing animation packet matches Java 26.3 protocol 777");
            }
            float previous = player2.Vitals.Snapshot().Health;
            await server.HandleAttackAsync(player1, player2.EntityId,
                CancellationToken.None);
            Check(player2.Vitals.Snapshot().Health == previous &&
                player1.Inventory.Held().Damage == 1,
                "Spam attacks cannot bypass cooldown or mint damage");
            player2.SetGameMode(PlayerGameMode.Creative);
            await Task.Delay(140);
            await server.HandleAttackAsync(player1, player2.EntityId,
                CancellationToken.None);
            Check(player2.Vitals.Snapshot().Health == previous,
                "Creative targets are invulnerable");
            Check(server.SummonSheepAt(player1) &&
                server.LivingEntitiesForTests.Length == 1,
                "Authorized summon creates a persistent world livestock entity");
            await Task.Delay(140);
            LivingEntity summoned = server.LivingEntitiesForTests.Single();
            await server.HandleAttackAsync(player1, summoned.EntityId,
                CancellationToken.None);
            Check(server.LivingEntitiesForTests.Single().Health < 8 &&
                player1.Inventory.Held().Damage == 2,
                "Sheep receive melee damage through the same validated pathway");
            float healthBeforeCancel = server.LivingEntitiesForTests.Single().Health;
            Check(player1.Inventory.EnchantHeld("sharpness", 1, enchantments),
                "Prepare enchanted weapon with cancellable ~onAttack");
            await Task.Delay(140);
            await server.HandleAttackAsync(player1, summoned.EntityId,
                CancellationToken.None);
            Check(server.LivingEntitiesForTests.Single().Health == healthBeforeCancel &&
                player1.Inventory.Held().Damage == 2,
                "cancelEvent prevents vanilla attack damage and weapon wear");
            long beforeCancelledPunch = stream2.Length;
            await server.HandlePunchAsync(player1, CancellationToken.None);
            Check(stream2.Length == beforeCancelledPunch,
                "cancelEvent suppresses observer-side swing animation");
            await server.HandlePunchAsync(player1, CancellationToken.None);
            Check(stream2.Length > beforeCancelledPunch,
                "Cancelled swing suppression is consumed only once");
            player1.RecordAttackSwingCancellation(true);
            player1.AdvanceClientTick();
            Check(!player1.ConsumeAttackSwingCancellation(),
                "A missing punch cannot suppress animation in the next tick");

            // A second, uncancelled attack kills the sheep. Gameplay now
            // executes the Items Shard's YAML loot skills instead of a
            // hardcoded mutton/white-wool drop.
            Check(player1.Inventory.TryCreativeSet(36, sword.ProtocolId, 1, true),
                "Clear attack-cancelling enchantment to test a real kill");
            await Task.Delay(140);
            await server.HandleAttackAsync(player1, summoned.EntityId,
                CancellationToken.None);
            Check(server.LivingEntitiesForTests.Length == 0 &&
                items.TryGetByIdentifier("minecraft:mutton", out var mutton) &&
                items.TryGetByIdentifier("minecraft:white_wool", out var wool) &&
                server.GroundItemsForTests.Any(x => x.ItemId == mutton.ProtocolId) &&
                server.GroundItemsForTests.Any(x => x.ItemId == wool.ProtocolId),
                "Killing a sheep commits the YAML-defined meat and wool skill drops");

            ShardItemDefinition beef = default;
            Check(managedMobs.TrySpawn("cow", 1, 80, 0,
                    out ShardEntityInstance cow) && cow.Health == 4 &&
                items.TryGetByIdentifier("minecraft:beef", out beef),
                "Spawn a YAML-configured cow in the shared Entities runtime");
            int beforeGeneric = server.GroundItemsForTests.Length;
            await Task.Delay(140);
            await server.HandleAttackAsync(player1, cow.EntityId,
                CancellationToken.None);
            Check(!managedMobs.Snapshot().Any(e => e.EntityId == cow.EntityId) &&
                server.GroundItemsForTests.Length == beforeGeneric + 1 &&
                server.GroundItemsForTests.Any(drop =>
                    drop.ItemId == beef.ProtocolId && drop.Count == 1) &&
                managedMobs.DrainDeaths().Length == 0,
                "Validated melee kills a generic mob and commits one YAML death reward");
            await server.TickEntitiesAsync(CancellationToken.None);
            Check(server.GroundItemsForTests.Length == beforeGeneric + 1,
                "Generic melee kill cannot award the same death loot twice");

            Check(managedMobs.TrySpawn("zombie", 1, 80, 0,
                    out ShardEntityInstance zombie) && zombie.Health == 4,
                "Spawn a health-bearing zombie for authoritative native loot");
            int beforeNative = server.GroundItemsForTests.Length;
            await Task.Delay(140);
            await server.HandleAttackAsync(player1, zombie.EntityId,
                CancellationToken.None);
            Check(!managedMobs.Snapshot().Any(e => e.EntityId == zombie.EntityId) &&
                server.GroundItemsForTests.Length == beforeNative + 1 &&
                server.GroundItemsForTests.Count(d =>
                    d.ItemId == nativeReward.ProtocolId) == 1 &&
                nativeMobLoot.Contexts.Count == 1 &&
                nativeMobLoot.Contexts[0].KilledByPlayer &&
                nativeMobLoot.Contexts[0].AttackerKind == "minecraft:player" &&
                nativeMobLoot.Contexts[0].DamageTypeIdentifier ==
                    "minecraft:player_attack",
                "Melee death prefers native loot with player attribution over YAML fallback");
            Check(managedMobs.TrySpawn("zombie", 1, 80, 0,
                    out var consoleZombie) &&
                managedMobs.TryKill(consoleZombie.EntityId, out _),
                "Prepare a non-player entity death");
            await server.TickEntitiesAsync(CancellationToken.None);
            Check(nativeMobLoot.Contexts.Count == 2 &&
                !nativeMobLoot.Contexts[1].KilledByPlayer &&
                nativeMobLoot.Contexts[1].AttackerKind is null &&
                server.GroundItemsForTests.Length == beforeNative + 2,
                "Console kills use native loot without player-only eligibility");

            string lootOverrides = Path.Combine(root, "loot-overrides");
            Directory.CreateDirectory(lootOverrides);
            File.WriteAllText(Path.Combine(lootOverrides, "mobs.yml"), """
                minecraft:cow:
                  Skills:
                    - "dropItem{item=minecraft:emerald;amount=1} @self ~onDeath"
                minecraft:zombie:
                  Skills: []
                """);
            var overrideLoot = new LootCatalog(Path.Combine(
                solutionRoot.FullName, "shards", "Amethyst.Shards.Items",
                "loot-26.3.yml"), lootOverrides);
            skills.RegisterInstructions("override-tests", overrideLoot.Instructions);
            Check(overrideLoot.HasOverride("minecraft:cow") &&
                overrideLoot.HasOverride("minecraft:zombie") &&
                overrideLoot.TryGetSkills("minecraft:zombie", out var emptyOverride) &&
                emptyOverride.Count == 0,
                "Administrator loot override preserves explicit empty Skills lists");
            var overriddenMobs = new EntityManager(mobTypes);
            var overrideServer = TestServerFactory.Create(settings, world,
                null!, world, items: items, recipes: recipes,
                entities: overriddenMobs, entityTypes: mobTypes,
                enchantments: enchantments, skills: skills, loot: overrideLoot,
                vanillaLoot: nativeMobLoot);
            using var overrideStream = new MemoryStream();
            using var overrideChannel = new PacketChannel(overrideStream);
            Guid overrideId = Guid.NewGuid();
            Check(overrideServer.TryAdmit(new VerifiedProfile(
                    overrideId, "OverrideTester", []), overrideChannel, 0, out _),
                "Admit a player for authoritative loot override integration");
            ConnectedPlayer overridePlayer =
                overrideServer.GetPlayerForTests(overrideId)!;
            overridePlayer.MarkReady();
            overridePlayer.SetGameMode(PlayerGameMode.Survival);
            overridePlayer.SetPose(new PlayerPose(0, 80, 0, 0, 0, true));
            ShardItemDefinition emerald = default;
            ShardEntityInstance overrideCow = default;
            Check(items.TryGetByIdentifier("minecraft:emerald", out emerald) &&
                overridePlayer.Inventory.TryCreativeSet(
                    36, sword.ProtocolId, 1, true) &&
                overriddenMobs.TrySpawn("cow", 1, 80, 0, out overrideCow),
                "Set up a custom emerald-only cow death skill");
            await overrideServer.HandleAttackAsync(
                overridePlayer, overrideCow.EntityId, CancellationToken.None);
            Check(overrideServer.GroundItemsForTests.Length == 1 &&
                overrideServer.GroundItemsForTests[0].ItemId == emerald.ProtocolId,
                "Custom mob loot replaces legacy starter drops, without stacking them");
            Check(overriddenMobs.TrySpawn("zombie", 1, 80, 0,
                out var silentZombie), "Set up a mob with Skills: []");
            await Task.Delay(140);
            await overrideServer.HandleAttackAsync(
                overridePlayer, silentZombie.EntityId, CancellationToken.None);
            Check(overrideServer.GroundItemsForTests.Length == 1 &&
                !overriddenMobs.Snapshot().Any(e => e.EntityId == silentZombie.EntityId),
                "Explicit empty loot override suppresses both native and legacy drops");

            Check(items.TryGetByIdentifier("minecraft:diamond", out var rewardDiamond) &&
                world.TrySetBlockState(0, 81, 1, 10, out _),
                "Prepare a Survival dirt block for onBlockDrop custom skills");
            await server.HandleBlockActionAsync(player1,
                new PlayerBlockAction(BlockInteractionProtocol.StartDestroyBlock,
                    0, 81, 1, 1, 101), CancellationToken.None);
            await Task.Delay(950);
            await server.HandleBlockActionAsync(player1,
                new PlayerBlockAction(BlockInteractionProtocol.StopDestroyBlock,
                    0, 81, 1, 1, 102), CancellationToken.None);
            Check(world.GetBlockState(0, 81, 1) == 0 &&
                server.GroundItemsForTests.Any(x =>
                    x.ItemId == rewardDiamond.ProtocolId && x.Count == 2) &&
                !server.GroundItemsForTests.Any(x => x.ItemId == 55),
                "onBlockDrop cancelEvent suppresses the dirt drop but commits later dropItem skills");

            player2.SetGameMode(PlayerGameMode.Survival);
            Check(player2.ActiveEffects.Apply("minecraft:resistance", 200, 0) &&
                player2.ActiveEffects.Apply("minecraft:absorption", 200, 0) &&
                Math.Abs(player2.ActiveEffects.Absorption - 4) < 0.01,
                "Set up Resistance I and four absorption hearts on a real player");
            float beforeMitigated = player2.Vitals.Snapshot().Health;
            await Task.Delay(520);
            bool mitigated = await server.DamagePlayerAsync(
                player2, 10, null, CancellationToken.None);
            Check(mitigated &&
                Math.Abs(player2.Vitals.Snapshot().Health -
                    (beforeMitigated - 4)) < 0.01 &&
                Math.Abs(player2.ActiveEffects.Absorption) < 0.01,
                "Gameplay damage applies 20% Resistance and absorbs four hearts before health");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }

    private sealed class RecordedVanillaMobLoot(int rewardId) :
        IShardVanillaLootTables
    {
        public int Count => 1;
        public List<ShardVanillaLootContext> Contexts { get; } = [];
        public bool HasTable(string id) => id == "minecraft:entities/zombie";
        public IReadOnlyList<ShardItemDrop> Roll(string id,
            ShardVanillaLootContext context)
        {
            if (!HasTable(id))
                throw new InvalidDataException("Unexpected native loot table.");
            Contexts.Add(context);
            return [new ShardItemDrop(rewardId, 1)];
        }
    }

    private sealed class LootTestContext(SkillEngine engine) : IShardContext
    {
        public string Id => "combat-tests";
        public ServerOptions Options => throw new NotSupportedException();
        public string DataDirectory => Path.GetTempPath();
        public void RegisterService<T>(T service) where T : class { }
        public T GetRequiredService<T>() where T : class => throw new NotSupportedException();
        public T? GetService<T>() where T : class => null;
        public void Log(string message) { }
        public void RegisterPlayPacketHandler(int packetId, ShardPlayPacketHandler handler) { }
        public void RegisterConsoleCommand(string name, ShardConsoleCommand handler) { }
        public void RegisterSkillTrigger(string trigger) => engine.RegisterTrigger(Id, trigger);
        public void RegisterSkillMechanic(string name, ShardSkillValidator validator,
            ShardSkillExecutor executor) => engine.RegisterMechanic(Id, name, validator, executor);
        public void RegisterSkillCondition(string name, ShardSkillConditionValidator validator,
            ShardSkillConditionEvaluator evaluator) => engine.RegisterCondition(Id, name, validator, evaluator);
        public void RegisterSkillDefinitions(string file, string? directory = null) =>
            engine.RegisterDefinitions(Id, file, directory);
        public void RegisterSkillInstructions(IEnumerable<ShardSkillInstruction> instructions) =>
            engine.RegisterInstructions(Id, instructions);
    }
}
