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
Check(catalog.Count == 43 && catalog.All.All(x => x.Skills.Count > 0),
    "All 43 Java 26.3 enchantments have skill definitions");
Check(catalog.TryGet("sharpness", out var sharpness) &&
    sharpness.MaxLevel == 5 && sharpness.Skills[0].Trigger == "onAttack",
    "Vanilla metadata and trigger loaded");
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
          Skills:
            - "modify{stat=damage;base=0.5;perLevel=0.75} @target ~onAttack ?level{min=2}"
        """);
    var overridden = new EnchantmentCatalog(
        Path.Combine(path, "enchantments-26.3.yml"), temporary);
    Check(overridden.TryGet("sharpness", out var modified) &&
        modified.MaxLevel == 6 && modified.Skills[0].Conditions.Count == 1 &&
        modified.Weight == sharpness.Weight, "Partial YAML override and conditions");
    var engine = new SkillEngine();
    var context = new TestSkillContext(engine);
    EnchantmentSkills.Register(context);
    engine.RegisterInstructions("items", overridden.Instructions);
    var values = new ShardSkillRuntime();
    values.SetNumber("damage", 4);
    bool dispatched = await engine.DispatchAsync(new ShardSkillEvent(
        "minecraft:sharpness", "onAttack", EnchantmentLevel:3) { Runtime = values });
    Check(dispatched && Math.Abs(values.Number("damage") - 6.75) < 0.001,
        "Core dispatches Items mechanic with configured level condition");
    var disabled = new ShardSkillRuntime();
    disabled.SetNumber("damage", 4);
    await engine.DispatchAsync(new ShardSkillEvent(
        "minecraft:sharpness", "onAttack", EnchantmentLevel:1) { Runtime = disabled });
    Check(disabled.Number("damage") == 4,
        "Skill condition suppresses effects outside level range");
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
