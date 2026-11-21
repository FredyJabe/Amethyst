using Amethyst;
using Amethyst.BuiltinShards;
using Amethyst.Shards;

internal static class ConsumableFeatureTests
{
    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new Exception("FAIL: " + reason);
        Console.WriteLine("PASS: " + reason);
    }

    public static async Task RunAsync(ItemCatalog items, RecipeCatalog recipes)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null &&
            !File.Exists(Path.Combine(root.FullName, "Amethyst.slnx")))
            root = root.Parent;
        if (root is null) throw new InvalidOperationException("Amethyst solution missing.");
        var consumables = new ConsumableCatalog(items, Path.Combine(
            root.FullName, "shards", "Amethyst.Shards.Items", "consumables-26.3.yml"));
        if (!items.TryGetByIdentifier("minecraft:bread", out var bread) ||
            !items.TryGetByIdentifier("minecraft:mushroom_stew", out var stew) ||
            !items.TryGetByIdentifier("minecraft:bowl", out var bowl) ||
            !items.TryGetByIdentifier("minecraft:golden_apple", out var goldenApple))
            throw new InvalidDataException("Missing 26.3 test consumables.");
        Check(consumables.Count == 42 &&
            consumables.TryGet(bread.ProtocolId, out var breadFood) &&
            breadFood.Nutrition == 5 && breadFood.DurationTicks == 32 &&
            consumables.TryGet(stew.ProtocolId, out var stewFood) &&
            stewFood.RemainderItemId > 0,
            "42 26.3 consumable profiles load; bread and stew have correct nutrition/remainders");

        string folder = Path.Combine(Path.GetTempPath(),
            "amethyst-consumables-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Guid healthId = Guid.NewGuid();
            var vitals = new PlayerVitals(folder, healthId);
            Check(vitals.Snapshot().Food == 20 &&
                !vitals.TryEat(5, 6, false),
                "Ordinary food cannot be consumed at full hunger");
            vitals.AddExhaustion(24);
            Check(vitals.Snapshot().Food == 19 &&
                Math.Abs(vitals.Snapshot().Saturation) < 0.001,
                "Hunger exhaustion uses saturation before food points");
            Check(vitals.TryEat(5, 6, false) &&
                new PlayerVitals(folder, healthId).Snapshot() is
                    { Food: 20, Saturation: 6 },
                "Consumed food restores hunger and saturation across restart");

            var settings = new ServerOptions {
                Seed = 9314, GeneratorVersion = 5, ViewDistance = 2,
                SaveGeneratedChunks = false,
                WorldPath = Path.Combine(folder, "world")
            };
            await using var world = new TerrainWorld(settings);
            using var outbound = new MemoryStream();
            using var channel = new PacketChannel(outbound);
            var server = new MinecraftServer(settings, world, null!, world,
                items: items, recipes: recipes, consumables: consumables);
            Guid playerId = Guid.NewGuid();
            Check(server.TryAdmit(new VerifiedProfile(playerId, "FoodTester", []),
                channel, 0, out _), "Consumables integration admits authenticated player");
            var player = server.GetPlayerForTests(playerId)!;
            player.MarkReady();
            player.SetGameMode(PlayerGameMode.Survival);
            player.Vitals.AddExhaustion(24);
            Check(player.Inventory.TryCreativeSet(36, bread.ProtocolId, 2, true),
                "Prepare a two-piece bread stack");
            Check(await server.HandleConsumableUseAsync(player,
                new UseItemAction(0, 40, 0, 0), CancellationToken.None),
                "Use Item starts server-authoritative consumption");
            await server.TickConsumablesAsync(CancellationToken.None,
                nowMilliseconds: Environment.TickCount64 + 4000);
            Check(player.Inventory.Held() == new ItemStack(bread.ProtocolId, 1) &&
                player.Vitals.Snapshot().Food == 20,
                "Completed held use restores food and consumes exactly one item");

            player.Vitals.AddExhaustion(28);
            Check(await server.HandleConsumableUseAsync(player,
                new UseItemAction(0, 41, 0, 0), CancellationToken.None),
                "Second consumption begins");
            await server.ReleaseUsingItemAsync(player, CancellationToken.None);
            await server.TickConsumablesAsync(CancellationToken.None,
                nowMilliseconds: Environment.TickCount64 + 4000);
            Check(player.Inventory.Held().Count == 1 &&
                player.Vitals.Snapshot().Food == 19,
                "Early release cancels consumption without hunger or inventory mutation");

            Check(player.Inventory.TryCreativeSet(36, stew.ProtocolId, 1, true),
                "Prepare bowl-bearing food");
            Check(await server.HandleConsumableUseAsync(player,
                new UseItemAction(0, 42, 0, 0), CancellationToken.None),
                "Stew consumption starts");
            await server.TickConsumablesAsync(CancellationToken.None,
                nowMilliseconds: Environment.TickCount64 + 4000);
            Check(player.Inventory.Snapshot().Slots.Any(s =>
                    s.ItemId == bowl.ProtocolId && s.Count == 1),
                "Survival stew consumption returns the configured bowl");

            Check(player.Inventory.TryCreativeSet(36, goldenApple.ProtocolId, 1, true) &&
                await server.HandleConsumableUseAsync(player,
                    new UseItemAction(0, 43, 0, 0), CancellationToken.None),
                "Always-edible golden apple starts use at full hunger");
            await server.TickConsumablesAsync(CancellationToken.None,
                nowMilliseconds: Environment.TickCount64 + 4000);
            Check(player.ActiveEffects.Has("minecraft:regeneration") &&
                player.ActiveEffects.Has("minecraft:absorption"),
                "Consumed golden apple applies server-owned regeneration and absorption");
            Check(player.ActiveEffects.Remove("minecraft:regeneration") &&
                !player.ActiveEffects.Has("minecraft:regeneration"),
                "Status effect removal updates server state");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }
}
