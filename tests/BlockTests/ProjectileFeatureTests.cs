using Amethyst;
using Amethyst.BuiltinShards;

internal static class ProjectileFeatureTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static async Task RunAsync(ItemCatalog items, RecipeCatalog recipes)
    {
        Amethyst.Shards.ShardItemDefinition arrow = default, trident = default,
            snowball = default;
        Check(items.TryGetByIdentifier("bow", out var bow) &&
            items.TryGetByIdentifier("arrow", out arrow) &&
            items.TryGetByIdentifier("trident", out trident) &&
            items.TryGetByIdentifier("snowball", out snowball),
            "Version-pinned projectile item and ammo definitions available");
        string root = Path.Combine(Path.GetTempPath(),
            "amethyst-projectile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new ServerOptions
            {
                Seed = 98721, GeneratorVersion = 5,
                SaveGeneratedChunks = false, ViewDistance = 2,
                WorldPath = Path.Combine(root, "world")
            };
            await using var world = new TerrainWorld(settings);
            int next = 3000;
            var physics = new ProjectileWorld(() => ++next);
            var source = new PlayerPose(0.5, 115, 0.5, 0, 0, true);
            Check(physics.TryLaunch(Guid.NewGuid(), ProjectileKind.Arrow, source,
                1.5, 6, new ItemStack(arrow.ProtocolId, 1),
                out var launched) && launched.Kind == ProjectileKind.Arrow &&
                launched.Vz > 0 && launched.AgeTicks == 0,
                "Projectile initial velocity follows player yaw and pitch");
            using var targetStream = new MemoryStream();
            using var channel = new PacketChannel(targetStream);
            var target = new ConnectedPlayer(Guid.NewGuid(), "ArrowTarget",
                [], 999, channel, world, null, items);
            target.MarkReady();
            target.SetPose(new PlayerPose(0.5, 115, 3.5, 0, 0, true));
            ProjectileImpact? impact = null;
            for (int i = 0; i < 20; i++)
            {
                ProjectileFrame frame = physics.Tick(world, [new ProjectilePlayer(target.PlayerUuid, target.Pose)], []);
                if (frame.Impacts.Length > 0)
                {
                    impact = frame.Impacts[0];
                    break;
                }
            }
            Check(impact is { } collision &&
                collision.PlayerHit == target.PlayerUuid &&
                collision.Projectile.EntityId == launched.EntityId &&
                physics.Snapshot().Length == 0,
                "Swept server physics collides with the player and removes projectile");
            var mob = new Amethyst.Shards.ShardEntityInstance(
                33333, Guid.NewGuid(), "minecraft:zombie", 136,
                0.5, 115.2, 3.5, 0, 0, 20);
            Check(physics.TryLaunch(Guid.NewGuid(), ProjectileKind.Arrow, source,
                1.5, 6, new ItemStack(arrow.ProtocolId, 1),
                out var mobArrow), "Launch a second arrow at a generic living mob");
            ProjectileImpact? mobImpact = null;
            for (int i = 0; i < 20; i++)
            {
                ProjectileFrame frame = physics.Tick(world, [], [mob]);
                if (frame.Impacts.Length == 0) continue;
                mobImpact = frame.Impacts[0];
                break;
            }
            Check(mobImpact is { } collidedMob &&
                collidedMob.MobHit == mob.EntityId &&
                collidedMob.Projectile.EntityId == mobArrow.EntityId,
                "Projectile collision uses the generic Entities Shard population");
            await ProjectileProtocol.SendSpawnAsync(channel, launched,
                CancellationToken.None);
            targetStream.Position = 0;
            Packet? spawn = await Protocol.ReadPacketAsync(
                targetStream, CancellationToken.None);
            Packet? motion = await Protocol.ReadPacketAsync(
                targetStream, CancellationToken.None);
            Check(spawn is { Id: 0x01 } && motion is
                { Id: DroppedItemProtocol.TeleportEntity } &&
                spawn.Value.Reader().ReadVarInt() == launched.EntityId,
                "Projectile uses Java 26.3 Add Entity and Teleport packets");

            using var playStream = new MemoryStream();
            using var conn = new PacketChannel(playStream);
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null &&
                !File.Exists(Path.Combine(repository.FullName, "Amethyst.slnx")))
                repository = repository.Parent;
            if (repository is null)
                throw new InvalidOperationException("Missing repository root.");
            var entityCatalog = new EntityCatalog(Path.Combine(
                repository.FullName, "shards", "Amethyst.Shards.Entities",
                "entities-26.3.yml"));
            var managed = new EntityManager(entityCatalog);
            int impactSkillsRun = 0;
            var skillEngine = new Amethyst.Shards.SkillEngine();
            skillEngine.RegisterTrigger("projectile-test", "onProjectileHit");
            skillEngine.RegisterMechanic("projectile-test", "testImpact",
                skill => { },
                (skill, evt, ct) =>
                {
                    impactSkillsRun++;
                    evt.Runtime.SetNumber("damage", 20);
                    return ValueTask.CompletedTask;
                });
            skillEngine.RegisterInstructions("projectile-test", [
                Amethyst.Shards.SkillEngine.Parse("minecraft:bow",
                    "testImpact @self ~onProjectileHit")
            ]);
            var server = TestServerFactory.Create(settings, world,
                null!, world, items: items, recipes: recipes,
                entities: managed, entityTypes: entityCatalog,
                skills: skillEngine);
            Guid playerId = Guid.NewGuid();
            Check(server.TryAdmit(new VerifiedProfile(playerId,
                "ProjectileTester", []), conn, 0, out _),
                "Player admitted for ranged attacks");
            var player = server.GetPlayerForTests(playerId)!;
            player.MarkReady();
            player.SetGameMode(PlayerGameMode.Survival);
            player.SetPose(new PlayerPose(0.5, 115, 0.5, 0, 0, true));
            Check(player.Inventory.TryCreativeSet(36, bow.ProtocolId, 1, true) &&
                player.Inventory.TryCreativeSet(37, arrow.ProtocolId, 3, true),
                "Bow and ammo are legitimately held");
            player.UseState.BeginCharge(0, bow.ProtocolId,
                Environment.TickCount64 - 2000);
            await server.ReleaseProjectileChargeAsync(player,
                CancellationToken.None);
            Check(server.ProjectilesForTests.Length == 1 &&
                server.ProjectilesForTests[0].Kind == ProjectileKind.Arrow &&
                player.Inventory.Held().Damage == 1 &&
                player.Inventory.Snapshot().Slots[37].Count == 2,
                "Charged bow consumes an arrow and wears the bow exactly once");
            Check(server.ProjectilesForTests[0].SourceWeapon.ItemId == bow.ProtocolId &&
                managed.TrySpawn("cow", 0.5, 115.3, 3.5,
                    out ShardEntityInstance targetCow),
                "Ranged projectile retains authoritative firing weapon and has a mob target");
            for (int t = 0; t < 20 && impactSkillsRun == 0; t++)
                await server.TickProjectilesAsync(CancellationToken.None);
            Check(impactSkillsRun == 1 &&
                !managed.Snapshot().Any(m => m.EntityId == targetCow.EntityId),
                "~onProjectileHit item skill runs once and changes actual mob damage");
            Check(player.Inventory.TryCreativeSet(36, trident.ProtocolId, 1, true),
                "Equip trident for throwing");
            player.UseState.BeginCharge(0, trident.ProtocolId,
                Environment.TickCount64 - 1000);
            await server.ReleaseProjectileChargeAsync(player,
                CancellationToken.None);
            Check(player.Inventory.Held().Count == 0 &&
                server.ProjectilesForTests.Any(p =>
                    p.Kind == ProjectileKind.Trident &&
                    p.Recoverable.ItemId == trident.ProtocolId &&
                    p.Recoverable.Damage == 1),
                "Thrown trident leaves the inventory and retains recoverable durability");
            Check(player.Inventory.TryCreativeSet(36, snowball.ProtocolId, 2, true),
                "Equip two throwable snowballs");
            await server.HandleProjectileUseAsync(player,
                new UseItemAction(0, 31, 0, 0),
                CancellationToken.None);
            Check(player.Inventory.Held().Count == 1 &&
                server.ProjectilesForTests.Any(p =>
                    p.Kind == ProjectileKind.Snowball),
                "Throwing snowballs consumes one item and creates a physical entity");

            if (!items.TryGetByIdentifier("minecraft:splash_potion",
                out var splashMaterial))
                throw new InvalidDataException("Missing splash potion definition.");
            var splashPayload = new ItemStack(splashMaterial.ProtocolId, 1)
                .WithComponents(new Dictionary<string, string>
                {
                    ["minecraft:potion_contents"] =
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            potion = "minecraft:water",
                            custom_effects = new[]
                            {
                                new { id = "minecraft:speed", duration = 200,
                                    amplifier = 1 }
                            }
                        })
                });
            Check(physics.TryLaunch(player.PlayerUuid,
                    ProjectileKind.SplashPotion, player.Pose,
                    1.5, 0, ItemStack.Empty, out var thrownPotion,
                    splashPayload) &&
                thrownPotion.Payload == splashPayload && !thrownPotion.CanRecover,
                "A physical splash potion retains its custom component payload without becoming pickable");
            ProjectileEntity potionImpact = thrownPotion with
                { X = player.Pose.X, Y = player.Pose.Y + 0.9,
                    Z = player.Pose.Z };
            await server.ApplySplashPotionAsync(potionImpact,
                player.PlayerUuid, [player], CancellationToken.None);
            Check(player.ActiveEffects.Snapshot().Any(effect =>
                    effect.Id == "minecraft:speed" &&
                    effect.RemainingTicks == 200 && effect.Amplifier == 1),
                "Splash impacts apply per-stack potion effects to nearby players");
            player.ActiveEffects.Remove("minecraft:speed");
            await server.ApplySplashPotionAsync(potionImpact with { X = 100 },
                Guid.Empty, [player], CancellationToken.None);
            Check(!player.ActiveEffects.Has("minecraft:speed"),
                "Splash potion effects cannot reach players outside the four-block radius");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
