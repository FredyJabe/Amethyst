using Amethyst;
using Amethyst.BuiltinShards;

internal static class FishingFeatureTests
{
    private sealed class PredictableRandom : Random
    {
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
    }

    private static void Check(bool yes, string description)
    {
        if (!yes) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    public static async Task RunAsync(ItemCatalog items, RecipeCatalog recipes)
    {
        Check(items.TryGetByIdentifier("fishing_rod", out var fishingRod) &&
            items.TryGetByIdentifier("cod", out _) &&
            FishingProtocol.BobberEntityType == 160,
            "Fishing rod, fish loot and Java 26.3 entity type are registered");

        string root = Path.Combine(Path.GetTempPath(),
            "amethyst-fishing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new ServerOptions
            {
                Seed = 83531, GeneratorVersion = 5, ViewDistance = 2,
                SaveGeneratedChunks = false,
                WorldPath = Path.Combine(root, "world")
            };
            await using var world = new TerrainWorld(settings);
            var fishing = new FishingWorld(() => Random.Shared.Next(20000, 30000));
            Guid player = Guid.NewGuid();
            var pose = new PlayerPose(0.5, 80, 0.5, 0, 0, true);
            FishingBobber first = default;
            Check(world.TrySetBlockState(0, 81, 1, 89, out _) &&
                fishing.TryCast(player, 120, pose, out first) &&
                !fishing.TryCast(player, 120, pose, out _),
                "Fishing casts one physical bobber per player");
            long now = 100_000;
            var bobbers = fishing.Tick(world, now, new PredictableRandom());
            Check(bobbers.Length == 1 && bobbers[0].InWater &&
                !bobbers[0].Biting && bobbers[0].BiteAtMs == now + 5000,
                "Bobber recognizes water and schedules a bite window");
            bobbers = fishing.Tick(world, now + 5100,
                new PredictableRandom());
            Check(bobbers.Single().Biting &&
                fishing.TryReel(player, out FishingReel result,
                    now + 5200) && result.Caught &&
                fishing.Snapshot().Length == 0,
                "Reeling while a fish bites awards one catch and removes bobber");
            Check(fishing.TryCast(player, 120, pose, out _) &&
                fishing.TryReel(player, out FishingReel miss, now + 5300) &&
                !miss.Caught && !fishing.TryReel(player, out _),
                "Reeling too early cannot mint fish or duplicate a catch");
            Check(fishing.TryCast(player, 120, pose, out _,
                    waitReductionSeconds: 5) &&
                fishing.Tick(world, now + 6000,
                    new PredictableRandom()).Single().BiteAtMs == now + 7000 &&
                fishing.Cancel(player),
                "Lure Stats can reduce the simulated bite delay to one second");

            var (item, xp) = FishingWorld.RollLoot(new PredictableRandom());
            Check(item == "minecraft:cod" && xp == 1,
                "Fishing loot selects a version-pinned fish and bounded XP");

            using var wire = new MemoryStream();
            using var wireChannel = new PacketChannel(wire);
            await FishingProtocol.SendSpawnAsync(wireChannel, first,
                CancellationToken.None);
            wire.Position = 0;
            Packet? spawn = await Protocol.ReadPacketAsync(wire,
                CancellationToken.None);
            Packet? metadata = await Protocol.ReadPacketAsync(wire,
                CancellationToken.None);
            Check(spawn is { Id: DroppedItemProtocol.AddEntity } &&
                metadata is { Id: DroppedItemProtocol.SetEntityData },
                "Fishing bobber spawns and sends authoritative hook metadata");

            using var playStream = new MemoryStream();
            using var connection = new PacketChannel(playStream);
            var server = new MinecraftServer(settings, world,
                null!, world, items: items, recipes: recipes);
            Guid uuid = Guid.NewGuid();
            Check(server.TryAdmit(new VerifiedProfile(uuid, "Angler", []),
                connection, 0, out _),
                "Player admitted for fishing integration");
            var actor = server.GetPlayerForTests(uuid)!;
            actor.MarkReady();
            actor.SetGameMode(PlayerGameMode.Survival);
            actor.SetPose(pose);
            Check(actor.Inventory.TryCreativeSet(36, fishingRod.ProtocolId, 1, true),
                "Fishing rod placed into hotbar");
            await server.HandleFishingUseAsync(actor,
                new UseItemAction(0, 30, 0, 0),
                CancellationToken.None);
            Check(server.FishingHooksForTests.Length == 1 &&
                actor.Inventory.Held().Damage == 0,
                "Casting creates bobber without spending durability");
            await server.HandleFishingUseAsync(actor,
                new UseItemAction(0, 31, 0, 0),
                CancellationToken.None);
            Check(server.FishingHooksForTests.Length == 0 &&
                actor.Inventory.Held().Damage == 1,
                "Reeling consumes one durability point and clears bobber");
            actor.SetGameMode(PlayerGameMode.Creative);
            await server.HandleFishingUseAsync(actor,
                new UseItemAction(0, 32, 0, 0),
                CancellationToken.None);
            await server.HandleFishingUseAsync(actor,
                new UseItemAction(0, 33, 0, 0),
                CancellationToken.None);
            Check(actor.Inventory.Held().Damage == 1,
                "Creative fishing never consumes rod durability");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
