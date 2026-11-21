using Amethyst;
using Amethyst.BuiltinShards;
using Amethyst.Shards;

static void Check(bool ok, string name)
{
    if (!ok) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

DirectoryInfo? root = new(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "Amethyst.slnx")))
    root = root.Parent;
if (root is null) throw new InvalidOperationException("No solution root.");
string path = Path.Combine(root.FullName, "shards", "Amethyst.Shards.Items");
var catalog = new EnchantmentCatalog(Path.Combine(path, "enchantments-26.3.yml"));
var items = new ItemCatalog(Path.Combine(path, "items-26.3.yml"));
Check(catalog.Count == 43 && catalog.All.All(x =>
    x.Skills.Count > 0 || x.Stats.Count > 0),
    "All 43 Java 26.3 enchantments have Stats or Skills definitions");
Check(catalog.All.SelectMany(x => x.Skills).All(x => x.Mechanic != "modify"),
    "No vanilla enchantment uses the removed modify mechanic");
Check(catalog.TryGet("sharpness", out var sharpness) &&
    sharpness.MaxLevel == 5 && sharpness.Stats.Count == 1 &&
    sharpness.Stats[0].Trigger == "onAttack",
    "Vanilla enchantment metadata and Stats trigger loaded");
Check(catalog.ConflictsWith("sharpness", "smite") &&
    !catalog.ConflictsWith("sharpness", "unbreaking"),
    "Mutual exclusion and independent effects");
// The Items Shard owns entity loot, expressed entirely as normal skills.
var loot = new LootCatalog(Path.Combine(path, "loot-26.3.yml"));
Check(loot.Count == 2 && loot.HasDefinition("minecraft:sheep") &&
    loot.HasDefinition("minecraft:fishing") &&
    loot.Instructions.Count == 23,
    "Sheep loot and six fishing catch options are defined in YAML");
var lootEngine = new SkillEngine();
var lootContext = new TestSkillContext(lootEngine);
EnchantmentSkills.Register(lootContext);
LootSkills.Register(lootContext, items);
lootEngine.RegisterInstructions("loot-tests", loot.Instructions);
var blueLoot = new ShardSkillRuntime();
blueLoot.SetFlag("not_sheared");
blueLoot.SetFlag("color_blue");
blueLoot.SetNumber("looting_level", 3);
await lootEngine.DispatchAsync(new ShardSkillEvent(
    "minecraft:sheep", "onDeath", Random: new Random(12))
    { Runtime = blueLoot });
if (!items.TryGetByIdentifier("minecraft:blue_wool", out var blueWool) ||
    !items.TryGetByIdentifier("minecraft:mutton", out var mutton))
    throw new InvalidDataException("Missing vanilla sheep loot items.");
Check(blueLoot.Drops.Count == 2 &&
    blueLoot.Drops.Single(x => x.ItemId == blueWool.ProtocolId).Count == 1 &&
    blueLoot.Drops.Single(x => x.ItemId == mutton.ProtocolId).Count is >= 1 and <= 5,
    "Sheep loot respects color flags and rolled Looting bonus");
var fishRoll = new ShardSkillRuntime();
fishRoll.SetNumber("loot_roll", 97);
await lootEngine.DispatchAsync(new ShardSkillEvent(
    "minecraft:fishing", "onFishing") { Runtime = fishRoll });
Check(items.TryGetByIdentifier("minecraft:bowl", out var bowl) &&
    fishRoll.Drops.Count == 1 && fishRoll.Drops[0].ItemId == bowl.ProtocolId,
    "Fishing loot resolves exactly one YAML-selected catch");
var fishTreasure = new ShardSkillRuntime();
fishTreasure.SetNumber("loot_roll", 99);
await lootEngine.DispatchAsync(new ShardSkillEvent(
    "minecraft:fishing", "onFishing") { Runtime = fishTreasure });
Check(items.TryGetByIdentifier("minecraft:nautilus_shell", out var nautilus) &&
    fishTreasure.Drops.Count == 1 &&
    fishTreasure.Drops[0].ItemId == nautilus.ProtocolId,
    "Fishing loot roll condition selects treasure without hardcoded item IDs");
var shearedLoot = new ShardSkillRuntime();
shearedLoot.SetFlag("color_blue");
await lootEngine.DispatchAsync(new ShardSkillEvent(
    "minecraft:sheep", "onDeath", Random: new Random(21))
    { Runtime = shearedLoot });
Check(shearedLoot.Drops.Count == 1 &&
    shearedLoot.Drops[0].ItemId == mutton.ProtocolId,
    "Sheared sheep never drop wool");

Check(items.TryGetByIdentifier("minecraft:diamond_sword", out var sword) &&
    catalog.CanApply("sharpness", sword) &&
    !catalog.CanApply("aqua_affinity", sword),
    "Supported item category validation");

// Version-pinned, full Mojang 26.3 loot-table input data is kept as a
// git submodule (1,447 tables), not reconstructed from the old drop map.
string vanillaRoot = Path.Combine(root.FullName, "vendor",
    "minecraft-26.3-data");
var nativeTables = new VanillaLootTables(Path.Combine(vanillaRoot,
    "data", "minecraft", "loot_table"), items);
var blockStates = new VanillaBlockStateLookup(Path.Combine(
    vanillaRoot, "blocks.json"));
Check(nativeTables.Count == 1447 && blockStates.Count > 20000 &&
    blockStates.TryGet(1, out var stateName, out _) &&
    stateName == "minecraft:stone",
    "Pinned 26.3 Mojang loot and blockstate registries loaded");
Check(items.TryGetByIdentifier("minecraft:cobblestone", out var cobble) &&
    nativeTables.Roll("minecraft:blocks/stone",
        new ShardVanillaLootContext())[0].ItemId == cobble.ProtocolId,
    "Stone drops cobblestone without Silk Touch");
Check(nativeTables.Roll("minecraft:blocks/stone",
        new ShardVanillaLootContext { SilkTouch = true })[0].ItemId ==
    items.All.Single(x => x.Identifier == "minecraft:stone").ProtocolId,
    "Stone drops its own block with Silk Touch");
if (!items.TryGetByIdentifier("minecraft:acacia_door", out var acaciaDoor))
    throw new InvalidDataException("Missing vanilla acacia door.");
var lowerDoor = nativeTables.Roll("minecraft:blocks/acacia_door",
    new ShardVanillaLootContext
    {
        BlockId = "minecraft:acacia_door",
        BlockProperties = new Dictionary<string, string>
            { ["half"] = "lower" }
    });
var upperDoor = nativeTables.Roll("minecraft:blocks/acacia_door",
    new ShardVanillaLootContext
    {
        BlockId = "minecraft:acacia_door",
        BlockProperties = new Dictionary<string, string>
            { ["half"] = "upper" }
    });
Check(lowerDoor.Count == 1 && lowerDoor[0].ItemId == acaciaDoor.ProtocolId &&
    upperDoor.Count == 0,
    "Official 26.3 match_block drops only the door's lower half");
if (!items.TryGetByIdentifier("minecraft:copper_ingot", out var drownedCopper))
    throw new InvalidDataException("Missing vanilla copper ingot.");
var drownedLoot = nativeTables.Roll("minecraft:entities/drowned",
    new ShardVanillaLootContext
    {
        Looting = 3, KilledByPlayer = true,
        Random = new ConstantRandom(0.01)
    });
Check(drownedLoot.Any(d => d.ItemId == drownedCopper.ProtocolId),
    "Looting changes random_chance_with_enchanted_bonus on drowned kills");

Check(items.TryGetByIdentifier("minecraft:diamond_ore", out var ore) &&
    nativeTables.Roll("minecraft:blocks/diamond_ore",
        new ShardVanillaLootContext { SilkTouch = true })[0].ItemId ==
    ore.ProtocolId,
    "Silk Touch returns the original diamond ore, not a diamond");
Check(items.TryGetByIdentifier("minecraft:diamond", out var diamondOreDrop) &&
    nativeTables.Roll("minecraft:blocks/diamond_ore",
        new ShardVanillaLootContext { Fortune = 3 })[0] is
        { } diamondDrop && diamondDrop.ItemId == diamondOreDrop.ProtocolId &&
        diamondDrop.Count is >= 1 and <= 4,
    "Vanilla ore_drops formula uses Fortune multipliers");
var nativeSheep = nativeTables.Roll("minecraft:entities/sheep",
    new ShardVanillaLootContext { EntityColor = "blue" });
Check(items.TryGetByIdentifier("minecraft:blue_wool", out var blueFromNative) &&
    nativeSheep.Any(d => d.ItemId == blueFromNative.ProtocolId) &&
    nativeSheep.Any(d => d.ItemId == mutton.ProtocolId),
    "Vanilla nested sheep loot tables select a colored wool and mutton");
var nativeSheared = nativeTables.Roll("minecraft:entities/sheep",
    new ShardVanillaLootContext { EntityColor = "blue", EntitySheared = true });
Check(nativeSheared.All(d => d.ItemId != blueFromNative.ProtocolId),
    "Vanilla sheep predicate omits wool after shearing");
Check(nativeTables.Roll("minecraft:gameplay/fishing/fish",
    new ShardVanillaLootContext()).Count == 1,
    "Vanilla fishing sub-table selects exactly one weighted fish");
if (!items.TryGetByIdentifier("minecraft:name_tag", out var nameTag))
    throw new InvalidDataException("Missing 26.3 name tag.");
var nativeTreasure = nativeTables.Roll("minecraft:gameplay/fishing",
    new ShardVanillaLootContext
    {
        Luck = 3, InOpenWater = true, Random = new TreasureRandom()
    });
Check(nativeTreasure.Count == 1 &&
    nativeTreasure[0].ItemId == nameTag.ProtocolId,
    "Official 26.3 fishing weights select nested treasure in open water");
var nativeClosed = nativeTables.Roll("minecraft:gameplay/fishing",
    new ShardVanillaLootContext
    {
        Luck = 3, InOpenWater = false, Random = new TreasureRandom()
    });
Check(nativeClosed.Count == 1 &&
    nativeClosed[0].ItemId != nameTag.ProtocolId,
    "Vanilla fishing treasure is blocked by non-open-water predicate");

var vanillaTags = new VanillaLootTags(Path.Combine(vanillaRoot,
    "data", "minecraft", "tags"));
Check(vanillaTags.Resolve("item", "#minecraft:cluster_max_harvestables")
    .Contains("minecraft:diamond_pickaxe"),
    "Version-pinned item tags include amethyst harvestable pickaxes");
if (!items.TryGetByIdentifier("minecraft:amethyst_shard", out var shard) ||
    !items.TryGetByIdentifier("minecraft:barrel", out var barrel) ||
    !items.TryGetByIdentifier("minecraft:music_disc_13", out var disc))
    throw new InvalidDataException("Missing vanilla tag/loot test items.");
var clusterDrops = nativeTables.Roll("minecraft:blocks/amethyst_cluster",
    new ShardVanillaLootContext
    {
        BlockId = "minecraft:amethyst_cluster",
        ToolIdentifier = "minecraft:diamond_pickaxe"
    });
Check(clusterDrops.Count == 1 &&
    clusterDrops[0].ItemId == shard.ProtocolId && clusterDrops[0].Count == 4,
    "minecraft:match_tool uses the real vanilla item tag");
var noHarvest = nativeTables.Roll("minecraft:blocks/amethyst_cluster",
    new ShardVanillaLootContext
    {
        BlockId = "minecraft:amethyst_cluster",
        ToolIdentifier = "minecraft:wooden_shovel"
    });
Check(noHarvest.Count == 1 &&
    noHarvest[0].ItemId == shard.ProtocolId && noHarvest[0].Count == 2,
    "Incorrect tools receive the vanilla reduced shard count");
var nativeBarrel = nativeTables.Roll("minecraft:blocks/barrel",
    new ShardVanillaLootContext
    {
        BlockId = "minecraft:barrel",
        BlockEntityComponents = new Dictionary<string, string>
        {
            ["minecraft:custom_name"] =
                System.Text.Json.JsonSerializer.Serialize("Named Barrel")
        }
    });
Check(nativeBarrel.Count == 1 && nativeBarrel[0].ItemId == barrel.ProtocolId &&
    nativeBarrel[0].ComponentData?.Contains("Named Barrel",
        StringComparison.Ordinal) == true,
    "Vanilla copy_components transfers authoritative block-entity names");
var discDrops = nativeTables.Roll("minecraft:entities/creeper",
    new ShardVanillaLootContext
    {
        AttackerKind = "minecraft:skeleton",
        KilledByPlayer = false
    });
Check(discDrops.Any(d => d.ItemId == disc.ProtocolId ||
    items.TryGetById(d.ItemId, out var candidate) &&
    candidate.Identifier.StartsWith("minecraft:music_disc_", StringComparison.Ordinal)),
    "Killer-entity tags activate creeper music-disc loot");
Check(items.TryGetByIdentifier("minecraft:music_disc_tears", out var ghastDisc),
    "Java 26.3 includes the reflected-fireball music disc");
var ghastProjectileLoot = nativeTables.Roll("minecraft:entities/ghast",
    new ShardVanillaLootContext
    {
        KilledByPlayer = true,
        DamageTypeIdentifier = "minecraft:fireball",
        DirectDamagerKind = "minecraft:fireball",
        Random = new ConstantRandom(0.0)
    });
var ghastOtherDamageLoot = nativeTables.Roll("minecraft:entities/ghast",
    new ShardVanillaLootContext
    {
        KilledByPlayer = true,
        DamageTypeIdentifier = "minecraft:in_fire",
        DirectDamagerKind = "minecraft:fireball",
        Random = new ConstantRandom(0.0)
    });
Check(ghastProjectileLoot.Any(d => d.ItemId == ghastDisc.ProtocolId) &&
    ghastOtherDamageLoot.All(d => d.ItemId != ghastDisc.ProtocolId),
    "Ghast special disc requires the authoritative projectile damage tag");

string temporary = Path.Combine(Path.GetTempPath(),
    "amethyst-enchant-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    string generatedLoot = Path.Combine(temporary, "generated-loot");
    Directory.CreateDirectory(generatedLoot);
    File.WriteAllText(Path.Combine(generatedLoot, "test.json"), """
        {
          "type":"minecraft:chest",
          "pools":[
            {
              "rolls":1,
              "entries":[{"type":"minecraft:item", "name":"minecraft:book",
                "modifier":{"type":"minecraft:enchant_randomly",
                            "options":["minecraft:sharpness"]}}]
            },
            {
              "rolls":1,
              "entries":[{"type":"minecraft:item", "name":"minecraft:book",
                "modifier":{"type":"minecraft:set_enchantments",
                            "enchantments":{"minecraft:unbreaking":3}}}]
            },
            {
              "rolls":1,
              "entries":[{"type":"minecraft:item", "name":"minecraft:book",
                "modifier":{"type":"minecraft:enchant_with_levels",
                            "levels":30, "options":["minecraft:sharpness"]}}]
            },
            {
              "rolls":1,
              "entries":[{"type":"minecraft:item", "name":"minecraft:diamond_hoe",
                "modifier":{"type":"minecraft:enchant_with_levels",
                            "levels":30, "options":"minecraft:unbreaking"}}]
            }
          ]
        }
        """);
    var generatedEnchanted = new VanillaLootTables(generatedLoot, items,
        tagsDirectory: Path.Combine(vanillaRoot, "data", "minecraft", "tags"),
        enchantments: catalog,
        enchantmentDataDirectory: Path.Combine(
            vanillaRoot, "data", "minecraft", "enchantment"));
    var enchantedLoot = generatedEnchanted.Roll("minecraft:test",
        new ShardVanillaLootContext { Random = new ConstantRandom(0.0) });
    if (!items.TryGetByIdentifier("minecraft:enchanted_book", out var book))
        throw new InvalidDataException("Enchanted book registry entry missing.");
    Check(enchantedLoot.Count == 4 &&
        enchantedLoot.All(d => ItemStackComponents.IsValid(d.ComponentData)) &&
        enchantedLoot.Take(3).All(d => d.ItemId == book.ProtocolId) &&
        enchantedLoot[0].ComponentData!.Contains("minecraft:sharpness",
            StringComparison.Ordinal) &&
        enchantedLoot[1].ComponentData!.Contains("minecraft:unbreaking",
            StringComparison.Ordinal) &&
        enchantedLoot[2].ComponentData!.Contains("minecraft:sharpness",
            StringComparison.Ordinal) &&
        enchantedLoot[3].ComponentData!.Contains("minecraft:unbreaking",
            StringComparison.Ordinal),
        "Vanilla random, explicit and pinned-level enchantments yield valid per-stack components");

    File.WriteAllText(Path.Combine(generatedLoot, "filtered.json"), """
        {
          "type":"minecraft:chest",
          "pools":[
            {
              "rolls":1,
              "entries":[{"type":"minecraft:item", "name":"minecraft:compass",
                "modifier":[
                  {"type":"minecraft:set_name", "target":"item_name",
                    "name":{"translate":"item.minecraft.compass"}},
                  {"type":"minecraft:filtered",
                    "item_filter":{"items":"minecraft:compass"},
                    "on_pass":{"type":"minecraft:set_count","count":2}}
                ]}]
            },
            {
              "rolls":1,
              "entries":[{"type":"minecraft:item", "name":"minecraft:stick",
                "modifier":{"type":"minecraft:filtered",
                  "item_filter":{"items":"minecraft:compass"},
                  "on_fail":{"type":"minecraft:discard"}}}]
            },
            {
              "rolls":1,
              "entries":[{"type":"minecraft:item", "name":"minecraft:stick",
                "modifier":{"type":"minecraft:filtered",
                  "item_filter":{"predicates":{"minecraft:map_id":{}}},
                  "on_fail":{"type":"minecraft:discard"}}}]
            }
          ]
        }
        """);
    File.WriteAllText(Path.Combine(generatedLoot, "instrument.json"), """
        {
          "type":"minecraft:chest",
          "pools":[{"rolls":1,"entries":[
            {"type":"minecraft:item", "name":"minecraft:goat_horn",
             "modifier":{"type":"minecraft:set_instrument",
                         "options":"#minecraft:regular_goat_horns"}}
          ]}]
        }
        """);
    var extendedTables = new VanillaLootTables(generatedLoot, items,
        tagsDirectory: Path.Combine(vanillaRoot, "data", "minecraft", "tags"),
        enchantments: catalog,
        enchantmentDataDirectory: Path.Combine(
            vanillaRoot, "data", "minecraft", "enchantment"));
    var hornLoot = extendedTables.Roll("minecraft:instrument",
        new ShardVanillaLootContext { Random = new Random(14) });
    Check(items.TryGetByIdentifier("minecraft:goat_horn", out var horn) &&
        hornLoot.Count == 1 && hornLoot[0].ItemId == horn.ProtocolId &&
        ItemStackComponents.IsValid(hornLoot[0].ComponentData) &&
        ItemStackComponents.Decode(hornLoot[0].ComponentData)
            .TryGetValue("minecraft:instrument", out var instrument) &&
        VanillaRegistries.IndexOf("minecraft:instrument",
            System.Text.Json.JsonSerializer.Deserialize<string>(instrument)!) >= 0,
        "Native goat-horn loot assigns a validated 26.3 instrument component");
    var filteredLoot = extendedTables.Roll("minecraft:filtered",
        new ShardVanillaLootContext { Random = new Random(19) });
    Check(items.TryGetByIdentifier("minecraft:compass", out var compass) &&
        filteredLoot.Count == 1 &&
        filteredLoot[0].ItemId == compass.ProtocolId &&
        filteredLoot[0].Count == 2 &&
        ItemStackComponents.IsValid(filteredLoot[0].ComponentData) &&
        ItemStackComponents.Decode(filteredLoot[0].ComponentData)
            .TryGetValue("minecraft:item_name", out var translatedName) &&
        translatedName.Contains("item.minecraft.compass", StringComparison.Ordinal),
        "Filtered and discarded loot is stack-specific; translated names survive the wire codec");

    File.WriteAllText(Path.Combine(temporary, "override.yml"), """
        sharpness:
          MaxLevel: 6
          Stats:
            damage:
              Trigger: onAttack
              Base: 0.5
              PerLevel: 0.75
              Conditions: ["level{min=2}"]
        """);
    var overridden = new EnchantmentCatalog(
        Path.Combine(path, "enchantments-26.3.yml"), temporary);
    Check(overridden.TryGet("sharpness", out var modified) &&
        modified.MaxLevel == 6 && modified.Stats[0].Conditions.Count == 1 &&
        modified.Weight == sharpness.Weight, "Partial YAML Stats override and conditions");
    var engine = new SkillEngine();
    var context = new TestSkillContext(engine);
    EnchantmentSkills.Register(context);
    engine.RegisterInstructions("items", overridden.Instructions);
    engine.ValidateConditions(modified.Stats[0].Conditions);
    var values = new ShardSkillRuntime();
    values.SetNumber("damage", 4);
    var levelThree = new ShardSkillEvent("minecraft:sharpness", "onAttack",
        EnchantmentLevel:3) { Runtime = values };
    foreach (var stat in modified.Stats) stat.Apply(levelThree, engine);
    Check(Math.Abs(values.Number("damage") - 6.75) < 0.001,
        "Stats apply Base and PerLevel using registered Core conditions");
    var disabled = new ShardSkillRuntime();
    disabled.SetNumber("damage", 4);
    var levelOne = new ShardSkillEvent("minecraft:sharpness", "onAttack",
        EnchantmentLevel:1) { Runtime = disabled };
    foreach (var stat in modified.Stats) stat.Apply(levelOne, engine);
    Check(disabled.Number("damage") == 4, "Stats condition suppresses low levels");
    var wrongEvent = new ShardSkillRuntime();
    wrongEvent.SetNumber("damage", 4);
    var eventMismatch = new ShardSkillEvent("minecraft:sharpness", "onMine",
        EnchantmentLevel:3) { Runtime = wrongEvent };
    foreach (var stat in modified.Stats) stat.Apply(eventMismatch, engine);
    Check(wrongEvent.Number("damage") == 4, "Stats trigger filter prevents wrong-event bonuses");

    var itemOverrides = Path.Combine(temporary, "item-overrides");
    Directory.CreateDirectory(itemOverrides);
    File.WriteAllText(Path.Combine(itemOverrides, "sword.yml"), """
        diamond_sword:
          Skills:
            - "cancelEvent @self ~onRightClick"
            - "setFlag{name=right_click_done} @self ~onRightClick"
          Stats:
            damage:
              Trigger: onAttack
              Base: 3
            knockback:
              - Trigger: onAttack
                Base: 0.5
              - Trigger: onAttack
                Operation: multiply
                Base: 2
        wooden_axe:
          Stats:
            - damage 50
            - damage +10
            - damage -5
            - damage *1.5
            - attack_speed 2
        wooden_shovel:
          Stats:
            damage: 12
        wooden_pickaxe:
          Stats:
            - damage =-5
            - mining_speed *1.5
        """);
    var statItems = new ItemCatalog(Path.Combine(path, "items-26.3.yml"), itemOverrides);
    Check(statItems.TryGetByIdentifier("diamond_sword", out var boosted) &&
        boosted.Stats?.Count == 3 &&
        (boosted.Components?.Count ?? 0) == (sword.Components?.Count ?? 0),
        "Items accept independent Stats without altering item components");
    var itemRuntime = new ShardSkillRuntime();
    itemRuntime.SetNumber("damage", 7);
    itemRuntime.SetNumber("knockback", 0.4);
    var itemEvent = new ShardSkillEvent(boosted.Identifier, "onAttack",
        EnchantmentLevel:1) { Runtime = itemRuntime };
    foreach (var stat in boosted.Stats ?? []) stat.Apply(itemEvent, engine);
    Check(Math.Abs(itemRuntime.Number("damage") - 10) < 0.001 &&
        Math.Abs(itemRuntime.Number("knockback") - 1.8) < 0.001,
        "Multiple item modifiers compose add and multiply in order");

    if (!statItems.TryGetByIdentifier("wooden_axe", out var axe))
        throw new Exception("Missing wooden axe definition.");
    Check(axe.Stats?.Count == 5 &&
        axe.Stats.Select(x => x.Operation).SequenceEqual(
            new[] { "set", "add", "add", "multiply", "set" }),
        "Stats sequence parses set, add, subtract and multiply in order");
    var axeRuntime = new ShardSkillRuntime();
    axeRuntime.SetNumber("damage", 7);
    axeRuntime.SetNumber("attack_speed", 4);
    var axeEvent = new ShardSkillEvent(axe.Identifier, "onAttack",
        EnchantmentLevel: 1) { Runtime = axeRuntime };
    foreach (var stat in axe.Stats ?? []) stat.Apply(axeEvent, engine);
    Check(Math.Abs(axeRuntime.Number("damage") - 82.5) < 0.001 &&
        axeRuntime.Number("attack_speed") == 2,
        "Ordered Stats operations compose: ((50 + 10 - 5) * 1.5) = 82.5");

    if (!statItems.TryGetByIdentifier("wooden_shovel", out var shovel))
        throw new Exception("Missing wooden shovel definition.");
    var shovelRuntime = new ShardSkillRuntime();
    shovelRuntime.SetNumber("damage", 7);
    var shovelEvent = new ShardSkillEvent(shovel.Identifier, "onAttack")
        { Runtime = shovelRuntime };
    foreach (var stat in shovel.Stats ?? []) stat.Apply(shovelEvent, engine);
    Check(shovelRuntime.Number("damage") == 12,
        "Legacy mapping scalar values also use absolute assignment");

    if (!statItems.TryGetByIdentifier("wooden_pickaxe", out var pickaxe))
        throw new Exception("Missing wooden pickaxe definition.");
    var pickaxeRuntime = new ShardSkillRuntime();
    pickaxeRuntime.SetNumber("damage", 8);
    pickaxeRuntime.SetNumber("mining_speed", 2);
    var pickaxeEvent = new ShardSkillEvent(pickaxe.Identifier, "onAttack")
        { Runtime = pickaxeRuntime };
    foreach (var stat in pickaxe.Stats ?? []) stat.Apply(pickaxeEvent, engine);
    Check(pickaxeRuntime.Number("damage") == -5 &&
        pickaxeRuntime.Number("mining_speed") == 3,
        "Explicit equals sets negative values and signed factors multiply");

    var invalidStats = Path.Combine(temporary, "invalid-stats");
    Directory.CreateDirectory(invalidStats);
    File.WriteAllText(Path.Combine(invalidStats, "malformed.yml"), """
        wooden_axe:
          Stats:
            - damage **2
        """);
    bool malformedStatRejected = false;
    try
    {
        _ = new ItemCatalog(Path.Combine(path, "items-26.3.yml"), invalidStats);
    }
    catch (InvalidDataException)
    {
        malformedStatRejected = true;
    }
    Check(malformedStatRejected, "Malformed operator Stats assignments are rejected");
    var smite = catalog.All.Single(x => x.Identifier == "minecraft:smite").Stats[0];
    engine.ValidateConditions(smite.Conditions);
    var undead = new ShardSkillRuntime();
    undead.SetNumber("damage", 5);
    smite.Apply(new ShardSkillEvent("minecraft:smite", "onAttack",
        EnchantmentLevel:2, TargetKind:"undead") { Runtime = undead }, engine);
    Check(Math.Abs(undead.Number("damage") - 10) < 0.001,
        "Stats conditions still distinguish undead targets");
    var living = new ShardSkillRuntime();
    living.SetNumber("damage", 5);
    smite.Apply(new ShardSkillEvent("minecraft:smite", "onAttack",
        EnchantmentLevel:2, TargetKind:"other") { Runtime = living }, engine);
    Check(living.Number("damage") == 5, "Stats conditions reject mismatched targets");

    // A durability point must consult ~onItemDamage, not the legacy
    // Unbreaking integer alone. These rules use the actual enchantment YAML.
    var unbreakingEngine = new SkillEngine();
    EnchantmentSkills.Register(new TestSkillContext(unbreakingEngine));
    unbreakingEngine.RegisterInstructions("items", catalog.Instructions);
    var wearSkills = new EnchantmentDurability(items, catalog, unbreakingEngine);
    Check(catalog.TryGet("unbreaking", out var unbreaking) &&
        unbreaking.Skills.Count == 2 &&
        unbreaking.Skills.All(s => s.Mechanic.Equals("cancelEvent",
            StringComparison.OrdinalIgnoreCase)) &&
        unbreaking.Skills.All(s => s.Conditions.Any(c =>
            c.Name.Equals("chance", StringComparison.OrdinalIgnoreCase))),
        "Unbreaking YAML uses cancelEvent guarded by level-sensitive chance");
    // Items now use the same skill engine as enchantments, with
    // non-interrupting cancellation of the default right-click action.
    Check(boosted.Skills is { Count: 2 } &&
        boosted.Skills.All(skill => skill.Trigger == "onRightClick"),
        "Item-level Skills parse without being Minecraft components");
    var itemSkillEngine = new SkillEngine();
    EnchantmentSkills.Register(new TestSkillContext(itemSkillEngine));
    itemSkillEngine.RegisterInstructions("item-tests", boosted.Skills ?? []);
    var rightClick = new ShardSkillEvent(boosted.Identifier, "onRightClick");
    Check(await itemSkillEngine.DispatchAsync(rightClick) &&
        rightClick.Runtime.IsCancelled &&
        rightClick.Runtime.HasFlag("right_click_done"),
        "cancelEvent on an item suppresses vanilla use but executes later skills");
    Check(items.TryGetByIdentifier("minecraft:diamond_pickaxe", out var wearPickaxe),
        "Durability test item is registered");
    var enchantedTool = new ItemStack(wearPickaxe.ProtocolId, 1)
        { Damage = 7, Unbreaking = 3 };
    var enchantedArmor = new ItemStack(wearPickaxe.ProtocolId, 1)
        { Damage = 7, Unbreaking = 3 };
    Check(wearSkills.PreventsWear(enchantedTool, false, new ConstantRandom(0.6)) &&
        !wearSkills.PreventsWear(enchantedArmor, true, new ConstantRandom(0.6)) &&
        wearSkills.PreventsWear(enchantedArmor, true, new ConstantRandom(0.1)),
        "Vanilla YAML independently controls tool and armor Unbreaking rolls");
    Check(!wearSkills.PreventsWear(enchantedTool with { Unbreaking = 0 }, false,
        new ConstantRandom(0.0)), "Without enchantment there is no durability protection");
    Check(DurabilityRules.ApplyWear(enchantedTool, wearPickaxe.MaxDurability, 3,
            random: new ConstantRandom(0.0), preventsWear: wearSkills.PreventsWear)
            == enchantedTool &&
        DurabilityRules.ApplyWear(enchantedTool, wearPickaxe.MaxDurability, 3,
            random: new ConstantRandom(0.99), preventsWear: wearSkills.PreventsWear)
            .Damage == 10,
        "Actual durability wear uses the configured skill result for every point");

    var mendingItem = enchantedTool with { Mending = true, Damage = 9 };
    Check(wearSkills.MendingRate(mendingItem) == 2,
        "Mending repair rate is read from enchantment Stats and Skills");

    string mendingDirectory = Path.Combine(temporary, "mending-override");
    Directory.CreateDirectory(mendingDirectory);
    File.WriteAllText(Path.Combine(mendingDirectory, "mending.yml"), """
        mending:
          Skills: []
        """);
    var disabledMendingCatalog = new EnchantmentCatalog(
        Path.Combine(path, "enchantments-26.3.yml"), mendingDirectory);
    var disabledMendingEngine = new SkillEngine();
    EnchantmentSkills.Register(new TestSkillContext(disabledMendingEngine));
    disabledMendingEngine.RegisterInstructions("items",
        disabledMendingCatalog.Instructions);
    Check(new EnchantmentDurability(items, disabledMendingCatalog,
        disabledMendingEngine).MendingRate(mendingItem) == 0,
        "Removing Mending Skills in YAML disables XP-based repair");

    File.WriteAllText(Path.Combine(mendingDirectory, "mending.yml"), """
        mending:
          Stats:
            mending_repair_per_xp:
              Trigger: onExperiencePickup
              Base: 4
        """);
    var fasterMendingCatalog = new EnchantmentCatalog(
        Path.Combine(path, "enchantments-26.3.yml"), mendingDirectory);
    var fasterMendingEngine = new SkillEngine();
    EnchantmentSkills.Register(new TestSkillContext(fasterMendingEngine));
    fasterMendingEngine.RegisterInstructions("items",
        fasterMendingCatalog.Instructions);
    var fasterMending = new EnchantmentDurability(items, fasterMendingCatalog,
        fasterMendingEngine);
    var mendingSlots = new ItemStack[46];
    mendingSlots[36] = mendingItem;
    var (xpLeft, didRepair, repairedSlot) = DurabilityRules.ApplyMending(
        mendingSlots, 36, 4, items, repairPerXp: fasterMending.MendingRate);
    Check(fasterMending.MendingRate(mendingItem) == 4 &&
        didRepair && repairedSlot == 36 && xpLeft == 1 &&
        mendingSlots[36].Damage == 0,
        "Mending Stats overrides change actual XP cost and repair amount");

    string inactiveDir = Path.Combine(temporary, "inactive-unbreaking");
    Directory.CreateDirectory(inactiveDir);
    File.WriteAllText(Path.Combine(inactiveDir, "unbreaking.yml"), """
        unbreaking:
          Skills: []
        """);
    var inactiveCatalog = new EnchantmentCatalog(
        Path.Combine(path, "enchantments-26.3.yml"), inactiveDir);
    var inactiveEngine = new SkillEngine();
    EnchantmentSkills.Register(new TestSkillContext(inactiveEngine));
    inactiveEngine.RegisterInstructions("items", inactiveCatalog.Instructions);
    var inactiveWear = new EnchantmentDurability(items, inactiveCatalog, inactiveEngine);
    Check(!inactiveWear.PreventsWear(enchantedTool, false, new ConstantRandom(0)),
        "Clearing Unbreaking Skills in YAML disables its protective effect");

    File.WriteAllText(Path.Combine(inactiveDir, "unbreaking.yml"), """
        unbreaking:
          Skills:
            - "cancelEvent @self ~onItemDamage"
        """);
    var alwaysCatalog = new EnchantmentCatalog(
        Path.Combine(path, "enchantments-26.3.yml"), inactiveDir);
    var alwaysEngine = new SkillEngine();
    EnchantmentSkills.Register(new TestSkillContext(alwaysEngine));
    alwaysEngine.RegisterInstructions("items", alwaysCatalog.Instructions);
    var alwaysWear = new EnchantmentDurability(items, alwaysCatalog, alwaysEngine);
    Check(alwaysWear.PreventsWear(enchantedTool, true, new ConstantRandom(0.99)),
        "Overriding Unbreaking with unconditional cancelEvent prevents wear");

    // A normal flag is not event cancellation. Cancellation does not stop
    // any remaining skill instructions in either dispatch mode.
    File.WriteAllText(Path.Combine(inactiveDir, "unbreaking.yml"), """
        unbreaking:
          Skills:
            - "setFlag{name=prevent_item_damage} @self ~onItemDamage"
        """);
    var flagOnlyCatalog = new EnchantmentCatalog(
        Path.Combine(path, "enchantments-26.3.yml"), inactiveDir);
    var flagOnlyEngine = new SkillEngine();
    EnchantmentSkills.Register(new TestSkillContext(flagOnlyEngine));
    flagOnlyEngine.RegisterInstructions("items", flagOnlyCatalog.Instructions);
    Check(!new EnchantmentDurability(items, flagOnlyCatalog, flagOnlyEngine)
        .PreventsWear(enchantedTool, false, new ConstantRandom(0)),
        "setFlag cannot cancel a durability event");

    var cancellationEngine = new SkillEngine();
    EnchantmentSkills.Register(new TestSkillContext(cancellationEngine));
    cancellationEngine.RegisterInstructions("cancellation-tests", [
        SkillEngine.Parse("test_cancel", "setFlag{name=before} @self ~onItemDamage"),
        SkillEngine.Parse("test_cancel",
            "cancelEvent @self ~onItemDamage ?level{min=2} ?chance{base=1}"),
        SkillEngine.Parse("test_cancel", "setFlag{name=after} @self ~onItemDamage")
    ]);
    var cancelContext = new ShardSkillEvent("test_cancel", "onItemDamage",
        EnchantmentLevel: 2);
    Check(cancellationEngine.DispatchImmediate(cancelContext) &&
        cancelContext.Runtime.IsCancelled &&
        cancelContext.Runtime.HasFlag("before") &&
        cancelContext.Runtime.HasFlag("after"),
        "cancelEvent marks vanilla event cancelled but runs subsequent sync skills");
    var allowedContext = new ShardSkillEvent("test_cancel", "onItemDamage",
        EnchantmentLevel: 1);
    Check(cancellationEngine.DispatchImmediate(allowedContext) &&
        !allowedContext.Runtime.IsCancelled &&
        allowedContext.Runtime.HasFlag("after"),
        "Failed cancelEvent condition leaves event running");
    var asyncContext = new ShardSkillEvent("test_cancel", "onItemDamage",
        EnchantmentLevel: 2);
    Check(await cancellationEngine.DispatchAsync(asyncContext) &&
        asyncContext.Runtime.IsCancelled &&
        asyncContext.Runtime.HasFlag("after"),
        "cancelEvent keeps executing subsequent async skills");
    bool rejectedArgs = false;
    try
    {
        cancellationEngine.RegisterInstructions("invalid-cancel", [
            SkillEngine.Parse("invalid_cancel",
                "cancelEvent{something=true} @self ~onItemDamage")
        ]);
    }
    catch (InvalidDataException) { rejectedArgs = true; }
    Check(rejectedArgs, "cancelEvent rejects unknown parameters at registration");

    // A cancelled event can also reach other skill packs (e.g. separate
    // enchantments), all sharing the same event runtime.
    cancellationEngine.RegisterInstructions("other-enchantment", [
        SkillEngine.Parse("test_cancel", "setFlag{name=other_pack} @self ~onItemDamage")
    ]);
    var sharedRuntime = new ShardSkillRuntime();
    await cancellationEngine.DispatchAsync(new ShardSkillEvent(
        "test_cancel", "onItemDamage", EnchantmentLevel: 2)
        { Runtime = sharedRuntime });
    Check(sharedRuntime.IsCancelled && sharedRuntime.HasFlag("after") &&
        sharedRuntime.HasFlag("other_pack"),
        "Cancellation preserves later skills from multiple skill packs");

    var attackRuntime = new ShardSkillRuntime();
    cancellationEngine.RegisterInstructions("attack-cancel", [
        SkillEngine.Parse("test_attack", "cancelEvent @self ~onAttack"),
        SkillEngine.Parse("test_attack", "setFlag{name=attack_after} @self ~onAttack")
    ]);
    await cancellationEngine.DispatchAsync(new ShardSkillEvent(
        "test_attack", "onAttack") { Runtime = attackRuntime });
    Check(attackRuntime.IsCancelled && attackRuntime.HasFlag("attack_after"),
        "~onAttack cancellation still executes subsequent attack skills");

    string componentDir = Path.Combine(temporary, "component-enchantments");
    Directory.CreateDirectory(componentDir);
    File.WriteAllText(Path.Combine(componentDir, "pickaxe.yml"), """
        diamond_pickaxe:
          Components:
            Enchantments: {minecraft:unbreaking: 3}
        """);
    var componentItems = new ItemCatalog(Path.Combine(path, "items-26.3.yml"),
        componentDir);
    var componentWear = new EnchantmentDurability(componentItems, catalog,
        unbreakingEngine);
    Check(componentWear.PreventsWear(new ItemStack(pickaxe.ProtocolId, 1), false,
        new ConstantRandom(0.0)),
        "Definition-level Enchantments component also activates the skill");
    var inventory = new PlayerInventory(temporary, Guid.NewGuid(), items);
    Check(inventory.TryCreativeSet(36, sword.ProtocolId, 1, true) &&
        inventory.EnchantHeld("sharpness", 5, catalog) &&
        inventory.Held().EnchantmentLevel("sharpness") == 5 &&
        !inventory.EnchantHeld("smite", 1, catalog) &&
        inventory.EnchantHeld("unbreaking", 3, catalog) &&
        inventory.Held().EnchantmentLevel("unbreaking") == 3,
        "Per-stack enchantments, conflict rejection and legacy Unbreaking");
    Guid playerId = Guid.NewGuid();
    var saved = new PlayerInventory(temporary, playerId, items);
    Check(saved.TryCreativeSet(36, sword.ProtocolId, 1, true) &&
        saved.EnchantHeld("sharpness", 4, catalog), "Enchanting persisted item");
    var reloaded = new PlayerInventory(temporary, playerId, items);
    Check(reloaded.Held().EnchantmentLevel("sharpness") == 4,
        "Enchantment levels survive inventory reload");
    string lootOverrides = Path.Combine(temporary, "loot-override");
    Directory.CreateDirectory(lootOverrides);
    File.WriteAllText(Path.Combine(lootOverrides, "sheep.yml"), """
        minecraft:sheep:
          Skills: []
        """);
    var emptyLoot = new LootCatalog(Path.Combine(path, "loot-26.3.yml"),
        lootOverrides);
    Check(emptyLoot.HasDefinition("minecraft:sheep") &&
        emptyLoot.HasDefinition("minecraft:fishing") &&
        emptyLoot.Instructions.Count == 6 &&
        emptyLoot.Instructions.All(skill => skill.OwnerId == "minecraft:fishing"),
        "Loot Skills: [] disables only that entity's drops, keeping other packs");
    File.WriteAllText(Path.Combine(lootOverrides, "sheep.yml"), """
        minecraft:sheep:
          Skills:
            - "cancelEvent @self ~onDeath"
            - "dropItem{item=minecraft:diamond;amount=2} @self ~onDeath"
        """);
    var customLoot = new LootCatalog(Path.Combine(path, "loot-26.3.yml"),
        lootOverrides);
    var customLootEngine = new SkillEngine();
    var customContext = new TestSkillContext(customLootEngine);
    EnchantmentSkills.Register(customContext);
    LootSkills.Register(customContext, items);
    customLootEngine.RegisterInstructions("custom-loot", customLoot.Instructions);
    var customDeath = new ShardSkillEvent("minecraft:sheep", "onDeath");
    await customLootEngine.DispatchAsync(customDeath);
    Check(items.TryGetByIdentifier("minecraft:diamond", out var diamond) &&
        customDeath.Runtime.IsCancelled && customDeath.Runtime.Drops.Count == 1 &&
        customDeath.Runtime.Drops[0] == new ShardItemDrop(diamond.ProtocolId, 2),
        "cancelEvent leaves later dropItem skills executable");
    bool invalidLootItem = false;
    try
    {
        customLootEngine.RegisterInstructions("bad-loot", [
            SkillEngine.Parse("minecraft:fake_mob",
                "dropItem{item=minecraft:not_a_real_item;amount=1} @self ~onDeath")
        ]);
    }
    catch (InvalidDataException) { invalidLootItem = true; }
    Check(invalidLootItem, "Unknown loot material rejected during registration");

    File.WriteAllText(Path.Combine(temporary, "invalid.yml"), """
        nonexistent:
          MaxLevel: 1
        """);
    bool rejected = false;
    try { _ = new EnchantmentCatalog(Path.Combine(path, "enchantments-26.3.yml"), temporary); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Unknown enchantment override rejected");
}
finally
{
    Directory.Delete(temporary, recursive: true);
}

sealed class TestSkillContext(SkillEngine engine) : IShardContext
{
    public string Id => "items";
    public ServerOptions Options => throw new NotSupportedException();
    public string DataDirectory => Path.GetTempPath();
    public void RegisterService<T>(T service) where T : class { }
    public T GetRequiredService<T>() where T : class => throw new NotSupportedException();
    public T? GetService<T>() where T : class => null;
    public void Log(string message) { }
    public void RegisterPlayPacketHandler(int packetId, ShardPlayPacketHandler handler) { }
    public void RegisterConsoleCommand(string name, ShardConsoleCommand handler) { }
    public void RegisterSkillTrigger(string trigger) => engine.RegisterTrigger(Id, trigger);
    public void RegisterSkillMechanic(string name, ShardSkillValidator validator,
        ShardSkillExecutor executor) => engine.RegisterMechanic(Id, name, validator, executor);
    public void RegisterSkillCondition(string name, ShardSkillConditionValidator validator,
        ShardSkillConditionEvaluator evaluator) => engine.RegisterCondition(Id, name, validator, evaluator);
    public void RegisterSkillDefinitions(string file, string? directory = null) =>
        engine.RegisterDefinitions(Id, file, directory);
    public void RegisterSkillInstructions(IEnumerable<ShardSkillInstruction> skills) =>
        engine.RegisterInstructions(Id, skills);
}

sealed class ConstantRandom(double fraction) : Random
{
    public override double NextDouble() => fraction;
}

sealed class TreasureRandom : Random
{
    public override double NextDouble() => 0.99;
    public override int Next(int maxValue) => maxValue > 50 ? 6 : 0;
}
