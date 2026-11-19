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
