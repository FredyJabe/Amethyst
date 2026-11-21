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

        Check(VanillaBuiltinIds.IndexOf("minecraft:potion",
                "minecraft:water") == 0 &&
            VanillaBuiltinIds.IndexOf("minecraft:potion",
                "minecraft:healing") >= 0 &&
            VanillaBuiltinIds.IndexOf("minecraft:mob_effect",
                "minecraft:speed") == 0 &&
            VanillaBuiltinIds.IndexOf("minecraft:mob_effect",
                "minecraft:poison") == 18 &&
            StatusEffectProtocol.TryGetId("minecraft:poison",
                out int poisonId) && poisonId == 18,
            "Potion and status effect IDs match the actual zero-based 26.3 registries");

        if (!items.TryGetByIdentifier("minecraft:potion", out var potionItem))
            throw new InvalidDataException("Missing potion item.");
        string potionContents = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                potion = "minecraft:healing",
                custom_effects = new[]
                {
                    new { id = "minecraft:speed", amplifier = 1, duration = 80 }
                }
            });
        var potionPatch = ItemStackComponents.Encode(
            new Dictionary<string, string>
            {
                ["minecraft:potion_contents"] = potionContents,
                ["minecraft:potion_duration_scale"] = "0.5"
            });
        var customPotion = new ItemStack(potionItem.ProtocolId, 1)
            { ComponentData = potionPatch };
        var potionEffects = ConsumableEffects.ForStack(customPotion, []);
        Check(ItemStackComponents.IsValid(potionPatch) &&
            potionEffects.Any(e => e.Id == "minecraft:instant_health" &&
                e.DurationTicks == 0) &&
            potionEffects.Any(e => e.Id == "minecraft:speed" &&
                e.DurationTicks == 40 && e.Amplifier == 1),
            "Per-stack potion_contents and duration scale drive item effects");

        var slotWriter = new PacketWriter();
        InventoryProtocol.WriteStack(slotWriter, customPotion, items);
        var slot = new PacketReader(slotWriter.ToArray());
        int slotCount = slot.ReadVarInt(), slotMaterial = slot.ReadVarInt();
        int addedComponents = slot.ReadVarInt(), removedComponents = slot.ReadVarInt();
        Check(slotCount == 1 && slotMaterial == potionItem.ProtocolId &&
            addedComponents == 2 && removedComponents == 0 &&
            ShardItemComponentIds.TryGetId("minecraft:potion_contents",
                out int potionComponentId) &&
            slot.ReadVarInt() == potionComponentId &&
            slot.ReadByte() == 1 && // Has potion
            slot.ReadVarInt() == VanillaBuiltinIds.IndexOf("minecraft:potion",
                "minecraft:healing") &&
            slot.ReadByte() == 0 && // No custom RGB
            slot.ReadVarInt() == 1 && // One custom effect
            slot.ReadVarInt() == 0 && // Speed id
            slot.ReadVarInt() == 1 && // Amplifier II
            slot.ReadVarInt() == 80 && // Duration before component scale
            slot.ReadByte() == 0 && slot.ReadByte() == 1 &&
            slot.ReadByte() == 1 && slot.ReadByte() == 0 &&
            slot.ReadByte() == 0 && // No custom potion name
            ShardItemComponentIds.TryGetId("minecraft:potion_duration_scale",
                out int scaleComponentId) &&
            slot.ReadVarInt() == scaleComponentId &&
            Math.Abs(slot.ReadFloat() - 0.5f) < 0.0001 &&
            slot.Remaining == 0,
            "Potion contents use the exact Java 26.3 slot network format");

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

            Guid effectsId = Guid.NewGuid();
            var active = new PlayerActiveEffects(folder, effectsId);
            Check(active.Apply("minecraft:absorption", 2400, 0) &&
                active.Apply("minecraft:resistance", 200, 0) &&
                Math.Abs(active.Absorption - 4) < 0.01f,
                "Absorption and resistance effects initialize authoritative hearts");
            var effectVitals = new PlayerVitals(null, Guid.NewGuid());
            Check(effectVitals.TryDamage(7, 5000, out bool effectKilled,
                    mitigation: active.Mitigate) && !effectKilled &&
                Math.Abs(effectVitals.Snapshot().Health - 18.4) < 0.01 &&
                Math.Abs(active.Absorption) < 0.01f,
                "Resistance reduces accepted damage before absorption hearts");
            Check(!effectVitals.TryDamage(7, 5200, out _,
                    mitigation: active.Mitigate) &&
                Math.Abs(effectVitals.Snapshot().Health - 18.4) < 0.01,
                "Invulnerability frames do not consume status mitigation");
            var restoredEffects = new PlayerActiveEffects(folder, effectsId);
            Check(restoredEffects.Has("minecraft:absorption") &&
                restoredEffects.Has("minecraft:resistance") &&
                Math.Abs(restoredEffects.Absorption) < 0.01,
                "Server potion effects persist across reconnects");

            var settings = new ServerOptions {
                Seed = 9314, GeneratorVersion = 5, ViewDistance = 2,
                SaveGeneratedChunks = false,
                WorldPath = Path.Combine(folder, "world")
            };
            await using var world = new TerrainWorld(settings);
            using var outbound = new MemoryStream();
            using var channel = new PacketChannel(outbound);
            var server = TestServerFactory.Create(settings, world, null!, world,
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
            // A custom potion's per-stack component data must survive the
            // inventory transaction and apply its custom effects, including
            // minecraft:potion_duration_scale. It returns a glass bottle.
            Check(player.Inventory.GiveStack(customPotion) == 1 &&
                await server.HandleConsumableUseAsync(player,
                    new UseItemAction(0, 44, 0, 0), CancellationToken.None),
                "Customized potion use begins with original component patch");
            await server.TickConsumablesAsync(CancellationToken.None,
                nowMilliseconds: Environment.TickCount64 + 4000);
            Check(player.ActiveEffects.Snapshot().Any(e =>
                    e.Id == "minecraft:speed" && e.RemainingTicks == 40) &&
                items.TryGetByIdentifier("minecraft:glass_bottle",
                    out var emptyBottle) &&
                player.Inventory.Snapshot().Slots.Any(v =>
                    v.ItemId == emptyBottle.ProtocolId && v.Count > 0),
                "Potion consumption applies configured effects and returns bottle");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }
}
