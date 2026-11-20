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
Check(items.TryGetByIdentifier("minecraft:diamond_sword", out var sword) &&
    catalog.CanApply("sharpness", sword) &&
    !catalog.CanApply("aqua_affinity", sword),
    "Supported item category validation");

string temporary = Path.Combine(Path.GetTempPath(),
    "amethyst-enchant-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
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
    Check(items.TryGetByIdentifier("minecraft:diamond_pickaxe", out var pickaxe),
        "Durability test item is registered");
    var enchantedTool = new ItemStack(pickaxe.ProtocolId, 1)
        { Damage = 7, Unbreaking = 3 };
    var enchantedArmor = new ItemStack(pickaxe.ProtocolId, 1)
        { Damage = 7, Unbreaking = 3 };
    Check(wearSkills.PreventsWear(enchantedTool, false, new ConstantRandom(0.6)) &&
        !wearSkills.PreventsWear(enchantedArmor, true, new ConstantRandom(0.6)) &&
        wearSkills.PreventsWear(enchantedArmor, true, new ConstantRandom(0.1)),
        "Vanilla YAML independently controls tool and armor Unbreaking rolls");
    Check(!wearSkills.PreventsWear(enchantedTool with { Unbreaking = 0 }, false,
        new ConstantRandom(0.0)), "Without enchantment there is no durability protection");
    Check(DurabilityRules.ApplyWear(enchantedTool, pickaxe.MaxDurability, 3,
            random: new ConstantRandom(0.0), preventsWear: wearSkills.PreventsWear)
            == enchantedTool &&
        DurabilityRules.ApplyWear(enchantedTool, pickaxe.MaxDurability, 3,
            random: new ConstantRandom(0.99), preventsWear: wearSkills.PreventsWear)
            .Damage == 10,
        "Actual durability wear uses the configured skill result for every point");

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
            - "setFlag{name=prevent_item_damage} @self ~onItemDamage"
        """);
    var alwaysCatalog = new EnchantmentCatalog(
        Path.Combine(path, "enchantments-26.3.yml"), inactiveDir);
    var alwaysEngine = new SkillEngine();
    EnchantmentSkills.Register(new TestSkillContext(alwaysEngine));
    alwaysEngine.RegisterInstructions("items", alwaysCatalog.Instructions);
    var alwaysWear = new EnchantmentDurability(items, alwaysCatalog, alwaysEngine);
    Check(alwaysWear.PreventsWear(enchantedTool, true, new ConstantRandom(0.99)),
        "Overriding Unbreaking Skills in YAML immediately changes the wear rule");

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
