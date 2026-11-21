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
    Check(entities is IShardEntityInteractions,
        "Entities manager provides the shared interaction API");
    Check(entities.TrySpawn("cow", 1, 80, 1, out var milkCow) &&
        entities.TryMilk(milkCow.EntityId, out _) &&
        entities.TryMilk(milkCow.EntityId, out _) &&
        !entities.TryShear(milkCow.EntityId, out _) &&
        entities.TryRemove(milkCow.EntityId) &&
        !entities.TryMilk(milkCow.EntityId, out _),
        "Cows may be milked repeatedly, but cannot be sheared or milked after despawn");
    Check(entities.TrySpawn("goat", 1, 80, 1, out var goat) &&
        entities.TryMilk(goat.EntityId, out _) &&
        entities.TryRemove(goat.EntityId) &&
        entities.TrySpawn("mooshroom", 1, 80, 1, out var mooshroom) &&
        entities.TryMilk(mooshroom.EntityId, out _) &&
        entities.TryRemove(mooshroom.EntityId),
        "Goats and mooshrooms provide milk interactions");
    Check(entities.TrySpawn("sheep", 1, 80, 1, out var sheep) &&
        !entities.IsSheared(sheep.EntityId) &&
        !entities.TryMilk(sheep.EntityId, out _) &&
        entities.TryShear(sheep.EntityId, out _) &&
        entities.IsSheared(sheep.EntityId) &&
        !entities.TryShear(sheep.EntityId, out _) &&
        entities.TryRemove(sheep.EntityId) &&
        !entities.IsSheared(sheep.EntityId),
        "Sheep can be sheared once; the sheared state is cleared after despawn");
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
    string skillVanilla = Path.Combine(root.FullName, "shards", "Amethyst.Shards.Items",
        "skills-26.3.yml");
    string itemVanilla = Path.Combine(root.FullName, "shards", "Amethyst.Shards.Items",
        "items-26.3.yml");
    var items = new ItemCatalog(itemVanilla);
    var drops = new TestDropSink();
    SkillEngine CreateEngine()
    {
        var engine = new SkillEngine();
        var mechanic = new DropItemSkillMechanic(items, () => drops);
        engine.RegisterMechanic("items", "dropItem", mechanic.Validate, mechanic.ExecuteAsync);
        return engine;
    }

    var starterSkills = CreateEngine();
    starterSkills.RegisterTrigger("entities", "onMilk");
    starterSkills.RegisterTrigger("entities", "onShear");
    starterSkills.RegisterDefinitions("items", skillVanilla);
    Check(starterSkills.TryGetSkills("sheep", out var shearing) &&
        shearing.Any(s => s.Trigger.Equals("onShear", StringComparison.OrdinalIgnoreCase)) &&
        starterSkills.OwnerCount >= 10 &&
        starterSkills.TryGetSkills("minecraft:cow", out var cowSkills) &&
        cowSkills.Count == 2 &&
        cowSkills.All(s => s.Trigger.Equals("onDeath", StringComparison.OrdinalIgnoreCase) &&
            s.Mechanic.Equals("dropItem", StringComparison.OrdinalIgnoreCase)),
        "Core parses Items Shard death skills with generic event/mechanic names");
    Check(await starterSkills.DispatchAsync(new ShardSkillEvent(
            "minecraft:cow", "onDeath", 2, 80, 3, Random: new Random(123))) &&
        drops.Items.Count > 0 &&
        items.TryGetByIdentifier("beef", out var beef) &&
        drops.Items.Any(d => d.ItemId == beef.ProtocolId && d.Count is >= 1 and <= 3 &&
            d.X == 2 && d.Z == 3),
        "Core dispatches Items dropItem with real resolved item effects");

    string skillsDir = Path.Combine(temp, "skills");
    Directory.CreateDirectory(skillsDir);
    string skillsFile = Path.Combine(skillsDir, "custom.yml");
    File.WriteAllText(skillsFile, """
        cow:
          Skills: []
        guardian_cow:
          Skills:
            - 'dropItem{i=minecraft:diamond_sword;a=3;c=1} @self ~onDeath'
            - 'dropItem{item=minecraft:beef;amount=1;chance=0} ~onDeath'
        """);
    var configured = CreateEngine();
    configured.RegisterTrigger("entities", "onMilk");
    configured.RegisterTrigger("entities", "onShear");
    configured.RegisterDefinitions("items", skillVanilla, skillsDir);
    drops.Items.Clear();
    Check(configured.TryGetSkills("minecraft:cow", out var disabledSkills) &&
        disabledSkills.Count == 0 &&
        await configured.DispatchAsync(new ShardSkillEvent(
            "minecraft:cow", "onDeath", Random: new Random(123))) &&
        drops.Items.Count == 0 &&
        configured.TryGetSkills("amethyst:guardian_cow", out var aliasSkills) &&
        aliasSkills.Count == 2,
        "Core respects custom entity aliases and explicitly empty skill lists");
    Check(await configured.DispatchAsync(new ShardSkillEvent(
            "amethyst:guardian_cow", "onDeath", 5, 71, 6,
            FallbackOwnerId: "minecraft:cow", Random: new Random(42))) &&
        drops.Items.Count == 3 &&
        drops.Items.All(d => d.Count == 1 && d.X == 5 && d.Z == 6) &&
        items.TryGetByIdentifier("diamond_sword", out var sword) &&
        drops.Items.All(d => d.ItemId == sword.ProtocolId),
        "Core executes Items skills honoring aliases, probability and stack limits");

    string testCustomPack = Path.Combine(temp, "other-shard.yml");
    File.WriteAllText(testCustomPack, """
        cow:
          Skills:
            - 'trace{tag=shared} ~onDeath'
            - 'trace{tag=spawned} ~onSpawn'
        """);
    var messages = new List<string>();
    configured.RegisterTrigger("entities", "onSpawn");
    configured.RegisterMechanic("other", "trace",
        skill => { if (!skill.Arguments.ContainsKey("tag")) throw new InvalidDataException("tag"); },
        (skill, evt, token) =>
        {
            messages.Add(skill.Arguments["tag"] + ":" + evt.Trigger);
            return ValueTask.CompletedTask;
        });
    configured.RegisterDefinitions("other", testCustomPack);
    drops.Items.Clear();
    Check(configured.OwnerCount >= 10 &&
        configured.TryGetSkills("cow", out var joined) &&
        joined.Count == 2 &&
        await configured.DispatchAsync(new ShardSkillEvent("cow", "onSpawn")) &&
        messages.SequenceEqual(new[] { "spawned:onSpawn" }) &&
        await configured.DispatchAsync(new ShardSkillEvent("cow", "onDeath")) &&
        messages.Last() == "shared:onDeath" &&
        drops.Items.Count == 0,
        "Core combines separate Shard skill packs and executes new mechanics/triggers");
    configured.Unregister("other");
    configured.Unregister("entities");
    messages.Clear();
    Check(configured.TryGetSkills("cow", out var restored) &&
        restored.Count == 0 &&
        await configured.DispatchAsync(new ShardSkillEvent("cow", "onDeath")) &&
        messages.Count == 0,
        "Core unregisters one Shard's skills without affecting other providers");
    configured.Unregister("items");
    Check(!configured.TryGetSkills("cow", out _) &&
        !await configured.DispatchAsync(new ShardSkillEvent("cow", "onDeath")),
        "Core removes skill definitions and mechanics on Shard disable");

    void RejectSkill(string yaml, string label)
    {
        File.WriteAllText(skillsFile, yaml);
        bool rejected = false;
        try
        {
            var checker = CreateEngine();
            checker.RegisterTrigger("entities", "onMilk");
            checker.RegisterTrigger("entities", "onShear");
            checker.RegisterDefinitions("items", skillVanilla, skillsDir);
        }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, label);
    }
    RejectSkill("""
        cow:
          Skills:
            - 'dropItem{item=minecraft:not_an_item;amount=1} ~onDeath'
        """, "Reject unknown item IDs in dropItem");
    RejectSkill("""
        cow:
          Skills:
            - 'dropItem{item=minecraft:beef;amount=5-2} ~onDeath'
        """, "Reject invalid item quantity ranges");
    RejectSkill("""
        cow:
          Skills:
            - 'dropItem{item=minecraft:beef;chance=2} ~onDeath'
        """, "Reject probabilities outside [0, 1]");
    RejectSkill("""
        cow:
          Skills:
            - 'dropItem{item=minecraft:beef} ~onSpawn'
        """, "Reject unregistered event triggers instead of ignoring them");
    RejectSkill("""
        cow:
          Skills:
            - 'teleport{to=spawn} ~onDeath'
        """, "Reject mechanics not registered by any Shard");
    RejectSkill("""
        cow:
          Skills:
            - 'dropItem{item=minecraft:beef;i=minecraft:diamond} ~onDeath'
        """, "Reject duplicate aliased parameters in the mechanic");
    RejectSkill("""
        cow:
          Skills: []
        cow:
          Skills: []
        """, "Reject duplicate entity skill roots");
    RejectSkill("""
        cow:
          Drops: []
        """, "Reject old Drops property; require Core Skills");
    RejectSkill("""
        cow:
          Skills:
            - 'dropItem{item=minecraft:beef;amount=1} @target ~onDeath'
        """, "Reject unsupported item-drop target selectors");

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

sealed class TestDropSink : IShardItemDropSink
{
    public readonly List<(int ItemId, int Count, double X, double Y, double Z)> Items = [];
    public bool TryDropItem(int itemId, int count, double x, double y, double z)
    {
        Items.Add((itemId, count, x, y, z));
        return true;
    }
}
