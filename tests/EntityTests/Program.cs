using Amethyst;
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
    ShardEntityDefinition vanillaZombie = default;
    Check(baseCatalog.TryGetByIdentifier("minecraft:cow", out var vanillaCow) &&
        vanillaCow.MaxHealth == 10 &&
        baseCatalog.TryGetByIdentifier("minecraft:zombie", out vanillaZombie) &&
        vanillaZombie.MaxHealth == 20 &&
        baseCatalog.TryGetByIdentifier("minecraft:iron_golem", out var golem) &&
        golem.MaxHealth == 100 &&
        baseCatalog.TryGetByIdentifier("minecraft:horse", out var horse) &&
        horse.MaxHealth is null &&
        baseCatalog.TryGetByIdentifier("minecraft:slime", out var slime) &&
        slime.MaxHealth is null,
        "Bundled health is authoritative for fixed-HP mobs; variable-HP mobs stay pending");
    Check(vanillaCow.ExperienceMin == 1 && vanillaCow.ExperienceMax == 3 &&
        vanillaZombie.ExperienceMin == 5 && vanillaZombie.ExperienceMax == 5 &&
        baseCatalog.TryGetByIdentifier("minecraft:blaze", out var blaze) &&
        blaze.ExperienceMin == 10 && blaze.ExperienceMax == 10,
        "Vanilla XP is a bounded scalar or inclusive range from entity YAML");

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
    Check(entities.TrySpawn("cow", 3, 80, 3, out var combatCow) &&
        entities.TryDamageAndTakeDeath(combatCow.EntityId, 3,
            out var wounded, out bool initiallyKilled) &&
        !initiallyKilled && wounded.Health == 37 &&
        entities.TryDamageAndTakeDeath(combatCow.EntityId, 100,
            out var combatDeath, out bool wasKilled) &&
        wasKilled && combatDeath.Health == 0 &&
        !entities.TryTakeDeath(combatCow.EntityId, out _) &&
        entities.DrainDeaths().Length == 0,
        "Atomic melee damage preserves surviving mobs and consumes each kill once");
    string skillVanilla = Path.Combine(root.FullName, "shards", "Amethyst.Shards.Items",
        "skills-26.3.yml");
    string itemVanilla = Path.Combine(root.FullName, "shards", "Amethyst.Shards.Items",
        "items-26.3.yml");
    var items = new ItemCatalog(itemVanilla);

    // The Entities Shard owns all physical world entities. One shared ID
    // allocator and world binding preserve existing save paths after moving
    // storage out of Gameplay; only projectiles and bobbers are transient.
    string ownedWorld = Path.Combine(temp, "entity-owned-world");
    var authoritative = new EntityManager(catalog);
    authoritative.BindWorldEntities(temp, ownedWorld, items);
    var native = (IShardLivingEntities)authoritative;
    var itemEntities = (IShardGroundItems)authoritative;
    var xpEntities = (IShardExperienceOrbs)authoritative;
    var fishingEntities = (IShardFishingWorld)authoritative;
    var projectiles = (IShardProjectileWorld)authoritative;
    GroundItem dropped = default;
    ExperienceOrb orb = default;
    Check(native.TrySpawnSheep(4, 80, 4, out LivingEntity redSheep, 14) &&
        native.TryShear(redSheep.EntityId, out var shearedRed) &&
        shearedRed.Sheared && shearedRed.Color == 14 &&
        authoritative.Snapshot().Any(x => x.EntityId == redSheep.EntityId) &&
        items.TryGetByIdentifier("minecraft:diamond", out var diamond) &&
        itemEntities.TrySpawn(diamond.ProtocolId, 2, 4, 80, 4,
            out dropped) &&
        xpEntities.TrySpawn(5, 4, 80, 4, out orb),
        "Entities owns colored sheep, ground items and XP with one allocator");
    Guid fisher = Guid.NewGuid();
    var launchPose = new PlayerPose(4, 80, 4, 0, 0, true);
    Check(fishingEntities.TryCast(fisher, 123, launchPose,
            out FishingBobber hook) &&
        projectiles.TryLaunch(fisher, ProjectileKind.Arrow, launchPose, 1.5f,
            2, ItemStack.Empty, out ProjectileEntity arrow) &&
        new[] { redSheep.EntityId, dropped.EntityId, orb.EntityId,
            hook.EntityId, arrow.EntityId }.Distinct().Count() == 5,
        "Entity ID allocation is unique across livestock, items, XP, hooks and projectiles");
    // Generic mobs now use their own AMGEN001 world-scoped store, separate
    // from the legacy sheep, dropped item and XP persistence formats.
    if (!authoritative.TrySpawn("cow", 7, 80, 7, out var managedCow))
        throw new Exception("Could not spawn persistent generic cow.");
    Check(authoritative.TryDamage(managedCow.EntityId, 6,
        out var woundedCow) && woundedCow.Health == 34 &&
        authoritative.TryTeleport(managedCow.EntityId, 8, 81, 8, 35, 0),
        "Generic mob health and teleport are writable persistent state");
    authoritative.SaveWorldEntities();
    var restartedWorld = new EntityManager(catalog);
    restartedWorld.BindWorldEntities(temp, ownedWorld, items);
    Check(((IShardLivingEntities)restartedWorld).Snapshot()
            .Any(x => x.Uuid == redSheep.Uuid && x.Sheared && x.Color == 14) &&
        ((IShardGroundItems)restartedWorld).Snapshot()
            .Any(x => x.Uuid == dropped.Uuid && x.Count == 2) &&
        ((IShardExperienceOrbs)restartedWorld).Snapshot()
            .Any(x => x.Value == 5) &&
        ((IShardFishingWorld)restartedWorld).Snapshot().Length == 0 &&
        ((IShardProjectileWorld)restartedWorld).Snapshot().Length == 0,
        "Entities restores durable living/item/XP state without resurrecting transient projectiles");

    ShardEntityInstance loadedCow = restartedWorld.Snapshot().Single(
        x => x.Uuid == managedCow.Uuid);
    Check(loadedCow.EntityId != managedCow.EntityId &&
        loadedCow.DefinitionId == managedCow.DefinitionId &&
        loadedCow.Health == 34 && loadedCow.X == 8 && loadedCow.Y == 81 &&
        loadedCow.Z == 8 && loadedCow.Yaw == 35 &&
        restartedWorld.Snapshot().Select(x => x.EntityId)
            .Distinct().Count() == restartedWorld.Snapshot().Length,
        "Generic mobs restore identity, health and position with fresh unique network IDs");
    Check(restartedWorld.TryDamageAndTakeDeath(loadedCow.EntityId, 100,
        out var deadCow, out bool killedPersisted) &&
        killedPersisted && deadCow.Uuid == managedCow.Uuid &&
        restartedWorld.DrainDeaths().Length == 0,
        "Lethal generic damage persists removal and consumes the one-shot death");
    var afterKill = new EntityManager(catalog);
    afterKill.BindWorldEntities(temp, ownedWorld, items);
    Check(!afterKill.Snapshot().Any(x => x.Uuid == managedCow.Uuid),
        "Killed generic mobs do not resurrect after restart");

    // The dead entity disappears before Gameplay can process the one-shot
    // onDeath event; its last color/sheared metadata must remain on the queued
    // snapshot or configurable native wool drops would be incorrect.
    var reloadedLivestock = (IShardLivingEntities)restartedWorld;
    Check(reloadedLivestock.TrySpawnSheep(6, 80, 6,
        out LivingEntity blueSheep, color: 11),
        "Spawn a blue sheep using the unified native entity store");
    bool killedBlue = restartedWorld.TryKill(blueSheep.EntityId,
        out ShardEntityInstance blueDeath);
    ShardEntityInstance[] deathQueue = restartedWorld.DrainDeaths();
    Check(killedBlue && blueDeath.Color == 11 && !blueDeath.Sheared &&
        deathQueue.Length == 1 && deathQueue[0].Color == 11 &&
        deathQueue[0].EntityId == blueSheep.EntityId &&
        restartedWorld.DrainDeaths().Length == 0,
        "Sheep death events retain server-owned wool metadata exactly once");

    var drops = new TestDropSink();
    SkillEngine CreateEngine()
    {
        var engine = new SkillEngine();
        var mechanic = new DropItemSkillMechanic(items, () => drops);
        engine.RegisterMechanic("items", "dropItem", mechanic.Validate, mechanic.ExecuteAsync);
        engine.RegisterCondition("items", "flag",
            condition =>
            {
                if (condition.Arguments.Count != 1 ||
                    !condition.Arguments.ContainsKey("name"))
                    throw new InvalidDataException("flag condition needs name.");
            },
            (condition, evt) =>
                evt.Runtime.HasFlag(condition.Arguments["name"]));
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

    drops.Items.Clear();
    var deferredDeath = new ShardSkillEvent("minecraft:cow", "onDeath",
        2, 80, 3, Random: new Random(123))
        { PublisherCommitsDrops = true };
    Check(await starterSkills.DispatchAsync(deferredDeath) &&
        deferredDeath.Runtime.Drops.Count > 0 &&
        deferredDeath.Runtime.Drops.All(d => d.ItemId > 0 &&
            d.Count is > 0 and <= 64) &&
        drops.Items.Count == 0,
        "Legacy dropItem stages loot for a publisher-owned event until the transaction commits");

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
