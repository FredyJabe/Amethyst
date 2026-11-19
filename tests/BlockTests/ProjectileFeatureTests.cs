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
                ProjectileFrame frame = physics.Tick(world, [target], []);
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
            var server = new MinecraftServer(settings, world,
                null!, world, items: items, recipes: recipes);
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
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
