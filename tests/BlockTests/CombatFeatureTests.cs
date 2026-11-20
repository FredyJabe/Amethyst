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
        var parserWriter = new PacketWriter();
        parserWriter.WriteVarInt(42);
        Check(CombatProtocol.ReadAttack(new Packet(
            CombatProtocol.ServerboundAttack,
            parserWriter.ToArray())) == 42,
            "Protocol 777 attack payload accepts precisely one entity ID");

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
            var enchantments = new EnchantmentCatalog(Path.Combine(
                solutionRoot.FullName, "shards", "Amethyst.Shards.Items",
                "enchantments-26.3.yml"));
            var skills = new SkillEngine();
            skills.RegisterTrigger("combat-tests", "onAttack");
            skills.RegisterInstructions("combat-tests", [
                SkillEngine.Parse("minecraft:sharpness",
                    "cancelEvent @self ~onAttack")
            ]);
            var server = new MinecraftServer(settings, world,
                null!, world, items: items, recipes: recipes,
                enchantments: enchantments, skills: skills);
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
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }
}
