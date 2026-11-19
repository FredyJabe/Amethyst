using Amethyst.BuiltinShards;
using Amethyst.Shards;

static void Check(bool ok, string message)
{
    if (!ok) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

DirectoryInfo? root = new(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "Amethyst.slnx")))
    root = root.Parent;
if (root is null) throw new InvalidOperationException("Solution not found.");
string vanilla = Path.Combine(root.FullName, "shards", "Amethyst.Shards.Entities",
    "entities-26.3.yml");
string temp = Path.Combine(Path.GetTempPath(), "amethyst-entity-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var baseCatalog = new EntityCatalog(vanilla);
    Check(baseCatalog.Count == 161 &&
        baseCatalog.TryGetById(72, out var item) && item.Identifier == "minecraft:item" &&
        baseCatalog.TryGetByIdentifier("minecraft:zombie", out var zombie) &&
        zombie.ProtocolId >= 0 && zombie.Category == "monster",
        "Vanilla entity registry reads all 161 wire types");

    string path = Path.Combine(temp, "override.yml");
    File.WriteAllText(path, """
        cow:
          Id: cow
          Health: 40
          Display: "Cattle"
          Options: { Spawnable: true }
        unbreakable_cow:
          Id: minecraft:cow
          Options: { Invulnerable: true, Health: 90 }
        """);
    var catalog = new EntityCatalog(vanilla, temp);
    Check(catalog.TryGetByIdentifier("cow", out var cow) &&
        cow.MaxHealth == 40 && cow.Display == "Cattle" &&
        catalog.TryGetByIdentifier("unbreakable_cow", out var alias) &&
        alias.Identifier == "amethyst:unbreakable_cow" &&
        alias.ProtocolId == cow.ProtocolId &&
        alias.Invulnerable && alias.MaxHealth == 90,
        "YAML overrides and aliases apply without changing the protocol IDs");

    var entities = new EntityManager(catalog);
    ShardEntityInstance two = default;
    Check(entities.TrySpawn("unbreakable_cow", 1, 80, 1, out var one) &&
        entities.TrySpawn("cow", 2, 80, 2, out two) &&
        one.EntityId != two.EntityId && one.Uuid != two.Uuid &&
        !entities.TrySpawn("item", 1, 80, 1, out _) &&
        !entities.TrySpawn("cow", double.PositiveInfinity, 1, 1, out _),
        "Spawning produces unique living entities and rejects unsupported types");
    Check(!entities.TryDamage(one.EntityId, 20, out _) &&
        entities.TryDamage(two.EntityId, 40, out var dead) &&
        dead.Health == 0 &&
        entities.Snapshot().Length == 1 &&
        entities.TryTeleport(one.EntityId, 4, 81, 5, 45, 10) &&
        entities.TryRemove(one.EntityId) &&
        entities.Snapshot().Length == 0,
        "Invulnerability, health, death and movement are authoritative");

    Check(entities.DrainDeaths() is { Length: 1 } &&
        entities.DrainDeaths().Length == 0,
        "Entity deaths are reported exactly once, independently of despawning");
    Check(entities.TrySpawn("cow", 1, 80, 1, out var toKill) &&
        entities.TryKill(toKill.EntityId, out var killed) &&
        killed.EntityId == toKill.EntityId &&
        entities.DrainDeaths() is { Length: 1 } &&
        !entities.TryKill(toKill.EntityId, out _),
        "Explicit kill reports death exactly once");
    string lootVanilla = Path.Combine(root.FullName, "shards", "Amethyst.Shards.Items",
        "loot-26.3.yml");
    string itemVanilla = Path.Combine(root.FullName, "shards", "Amethyst.Shards.Items",
        "items-26.3.yml");
    var itemCatalog = new ItemCatalog(itemVanilla);
    var vanillaLoot = new LootCatalog(itemCatalog, lootVanilla);
    Check(vanillaLoot.TableCount >= 10 &&
        vanillaLoot.TryRollEntity("minecraft:cow", out var cowLoot, new Random(123)) &&
        cowLoot.Length > 0 &&
        itemCatalog.TryGetByIdentifier("beef", out var beef) &&
        cowLoot.Any(d => d.ItemId == beef.ProtocolId && d.Count is >= 1 and <= 3),
        "Bundled entity loot is resolved through the Items Shard");

    string lootRoot = Path.Combine(temp, "loot");
    Directory.CreateDirectory(lootRoot);
    string lootFile = Path.Combine(lootRoot, "custom.yml");
    File.WriteAllText(lootFile, """
        cow:
          Drops: []
        guardian_cow:
          Drops:
            - { Item: minecraft:diamond_sword, Min: 3, Max: 3, Chance: 1 }
            - { Item: minecraft:beef, Min: 1, Max: 1, Chance: 0 }
        """);
    var customLoot = new LootCatalog(itemCatalog, lootVanilla, lootRoot);
    Check(customLoot.TryRollEntity("minecraft:cow", out var emptyLoot) &&
        emptyLoot.Length == 0 &&
        customLoot.TryRollEntity("amethyst:guardian_cow", out var aliasLoot, new Random(42)) &&
        aliasLoot.Length == 3 && aliasLoot.All(d => d.Count == 1) &&
        itemCatalog.TryGetByIdentifier("diamond_sword", out var sword) &&
        aliasLoot.All(d => d.ItemId == sword.ProtocolId),
        "Custom YAML overrides/aliases support empty tables, chances and splitting stacks");

    void RejectLoot(string yaml, string label)
    {
        File.WriteAllText(lootFile, yaml);
        bool rejected = false;
        try { _ = new LootCatalog(itemCatalog, lootVanilla, lootRoot); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, label);
    }
    RejectLoot("""
        cow:
          Drops:
            - { Item: minecraft:not_an_item, Min: 1, Max: 1 }
        """, "Loot rejects unknown item IDs");
    RejectLoot("""
        cow:
          Drops:
            - { Item: minecraft:beef, Min: 5, Max: 2 }
        """, "Loot rejects inverted counts");
    RejectLoot("""
        cow:
          Drops:
            - { Item: minecraft:beef, Chance: 2 }
        """, "Loot rejects probability above 1");
    RejectLoot("""
        cow:
          Drops: []
        cow:
          Drops: []
        """, "Loot rejects duplicate roots");
    RejectLoot("""
        cow:
          ExoticEffect: true
        """, "Loot rejects unsupported properties");

    File.WriteAllText(path, """
        cow:
          ProtocolId: 999
        """);
    ExpectInvalid("Reject configured protocol ID reassignment");
    File.WriteAllText(path, """
        test_mob:
          Id: minecraft:not_real
        """);
    ExpectInvalid("Reject aliases pointing to unknown registry types");
    File.WriteAllText(path, """
        cow:
          Options:
            Health: -10
        """);
    ExpectInvalid("Reject invalid health values");
    File.WriteAllText(path, """
        cow:
          Display: "one"
        cow:
          Display: "two"
        """);
    ExpectInvalid("Reject duplicate entity root keys");
    File.WriteAllText(path, """
        cow:
          UnsupportedKey: yes
        """);
    ExpectInvalid("Reject unrecognized entity configuration");

    void ExpectInvalid(string message)
    {
        bool rejected = false;
        try { _ = new EntityCatalog(vanilla, temp); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, message);
    }
}
finally
{
    try { Directory.Delete(temp, recursive: true); }
    catch (IOException) { }
}
Console.WriteLine("PASS: Entity catalog and runtime tests");
