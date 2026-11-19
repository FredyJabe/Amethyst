using Amethyst;
using Amethyst.Shards;

static void Check(bool ok, string message)
{
    if (!ok) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

DirectoryInfo? root = new(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "Amethyst.slnx")))
    root = root.Parent;
if (root is null) throw new InvalidOperationException("Amethyst.slnx not found above test output.");
string installed = Path.Combine(root.FullName, "shards", "installed");
if (!Directory.Exists(installed))
    throw new InvalidOperationException("Build Amethyst.slnx to install the test Shards first.");
string temp = Path.Combine(Path.GetTempPath(), "amethyst-shard-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var options = new ServerOptions
    {
        GeneratorVersion = 1,
        Seed = 100L,
        ViewDistance = 2,
        SaveGeneratedChunks = false,
        OperatorsFile = Path.Combine(temp, "ops.yaml"),
        WorldPath = Path.Combine(temp, "world")
    };
    // Test administrator YAML loaded through the real ShardHost data path.
    string yamlDir = Path.Combine(temp, "shards", "items", "definitions");
    Directory.CreateDirectory(yamlDir);
    File.WriteAllText(Path.Combine(yamlDir, "custom.yml"), """
        crafting_table:
          Display: "Workshop Table"
          Lore:
            - "Configured before the Shard loads"
        """);
    string recipeDir = Path.Combine(temp, "shards", "items", "recipes");
    Directory.CreateDirectory(recipeDir);
    File.WriteAllText(Path.Combine(recipeDir, "custom.yml"), """
        shovel_from_dirt:
          Type: Shapeless
          Ingredients: [dirt]
          Result: { Id: iron_shovel, Amount: 1 }
        """);
    string skillDir = Path.Combine(temp, "shards", "items", "skills");
    Directory.CreateDirectory(skillDir);
    File.WriteAllText(Path.Combine(skillDir, "custom.yml"), """
        cow:
          Skills:
            - 'dropItem{item=minecraft:diamond;amount=1} ~onDeath'
        """);
    string entityDir = Path.Combine(temp, "shards", "entities", "definitions");
    Directory.CreateDirectory(entityDir);
    File.WriteAllText(Path.Combine(entityDir, "custom.yml"), """
        cow:
          Display: "Farm Cow"
          Health: 30
        guardian_cow:
          Id: minecraft:cow
          Display: "Guardian Cow"
          Options:
            Health: 50
            Invulnerable: true
        """);
    // Exercise every Java 26.3 recipe serializer through the real Shard
    // loader. Special recipes remain registered but are not executable with
    // Gameplay's current component-free ItemStack.
    File.WriteAllText(Path.Combine(recipeDir, "all-types.yml"), """
        probe_campfire:
          Type: minecraft:campfire_cooking
          Ingredient: minecraft:beef
          CookingTime: 600
          Result: { Id: minecraft:cooked_beef, Amount: 1 }
        probe_stonecutting:
          Type: minecraft:stonecutting
          Ingredient: minecraft:stone
          Result: { Id: minecraft:stone_bricks, Amount: 1 }
        probe_smithing_transform:
          Type: minecraft:smithing_transform
          Template: minecraft:netherite_upgrade_smithing_template
          Base: minecraft:iron_sword
          Addition: minecraft:diamond
          Result: { Id: minecraft:netherite_sword, Amount: 1 }
        probe_smithing_trim:
          Type: minecraft:smithing_trim
          Template: minecraft:sentry_armor_trim_smithing_template
          Base: minecraft:iron_chestplate
          Addition: minecraft:diamond
        probe_brewing:
          Type: minecraft:brewing
          Input:
            Item: minecraft:potion
            PotionContents: { Potion: minecraft:water }
          Reagent: { Item: minecraft:nether_wart }
          Output:
            Id: minecraft:potion
            Components:
              'minecraft:potion_contents': { potion: minecraft:awkward }
        probe_transmute:
          Type: minecraft:crafting_transmute
          Input: minecraft:stone
          Material: minecraft:diamond
          Result: { Id: minecraft:diamond, Amount: 1 }
        probe_dye:
          Type: minecraft:crafting_dye
          Target: minecraft:leather_helmet
          Dye: minecraft:red_dye
          Result: { Id: minecraft:leather_helmet, Amount: 1 }
        probe_imbue:
          Type: minecraft:crafting_imbue
          Source: minecraft:lingering_potion
          Material: minecraft:arrow
          Result: { Id: minecraft:tipped_arrow, Amount: 8 }
        probe_decorated_pot:
          Type: minecraft:crafting_decorated_pot
          Back: minecraft:brick
          Left: minecraft:brick
          Right: minecraft:brick
          Front: minecraft:brick
          Result: { Id: minecraft:decorated_pot, Amount: 1 }
        probe_bannerduplicate:
          Type: minecraft:crafting_special_bannerduplicate

        probe_bookcloning:
          Type: minecraft:crafting_special_bookcloning

        probe_firework_rocket:
          Type: minecraft:crafting_special_firework_rocket
          Shell: minecraft:paper
          Fuel: minecraft:gunpowder
          Star: minecraft:firework_star
          Result: { Id: minecraft:firework_rocket, Amount: 3 }

        probe_firework_star:
          Type: minecraft:crafting_special_firework_star

        probe_firework_star_fade:
          Type: minecraft:crafting_special_firework_star_fade

        probe_mapextending:
          Type: minecraft:crafting_special_mapextending

        probe_repairitem:
          Type: minecraft:crafting_special_repairitem

        probe_shielddecoration:
          Type: minecraft:crafting_special_shielddecoration
        """);
    await using var host = new ShardHost(options, installed, Path.Combine(temp, "shards"));
    await host.LoadAsync();
    Check(host.GetService<IShardWorld>() is not null, "default overworld provider enabled");
    Check(host.GetService<IShardGameplay>() is not null, "gameplay enabled with world provider");
    IShardSkillEngine skillEngine = host.GetService<IShardSkillEngine>() ??
        throw new Exception("FAIL: Core skill engine not registered");
    var dropSink = host.GetService<IShardItemDropSink>() ??
        throw new Exception("FAIL: Gameplay item drop sink not registered");
    Check(skillEngine.OwnerCount >= 10 &&
        skillEngine.TryGetSkills("minecraft:cow", out var configuredSkills) &&
        configuredSkills.Count == 1 &&
        configuredSkills[0].Mechanic == "dropItem" &&
        host.RegisteredConsoleCommands.Contains("skillinfo") &&
        await host.TryExecuteConsoleAsync("skillinfo cow", CancellationToken.None),
        "Core skill engine is shared and Items registers its dropItem mechanic");
    IShardEntityRegistry entityTypes = host.GetService<IShardEntityRegistry>() ??
        throw new Exception("FAIL: Entities Shard service not registered");
    IShardEntityManager entityWorld = host.GetService<IShardEntityManager>() ??
        throw new Exception("FAIL: Entities runtime service not registered");
    IShardEntityInteractions entityActions = host.GetService<IShardEntityInteractions>() ??
        throw new Exception("FAIL: Entities interaction service not registered");
    Check(entityActions is not null &&
        skillEngine.TryGetSkills("sheep", out var shearSkills) &&
        shearSkills.Any(s => s.Trigger.Equals("onShear", StringComparison.OrdinalIgnoreCase)),
        "Entities registers ~onMilk/~onShear before Items loads sheep skills");
    ShardEntityDefinition cowType = default;
    ShardEntityInstance spawned = default, guardian = default;
    Check(entityTypes.MinecraftVersion == "26.3" && entityTypes.Count == 161 &&
        entityTypes.All.Count == 161 &&
        entityTypes.TryGetById(72, out var itemEntity) &&
        itemEntity.Identifier == "minecraft:item" &&
        entityTypes.TryGetByIdentifier("cow", out cowType) &&
        cowType.Display == "Farm Cow" && cowType.MaxHealth == 30,
        "Entities Shard exposes pinned 26.3 entity IDs and YAML overrides");
    Check(entityTypes.TryGetByIdentifier("guardian_cow", out var guardianType) &&
        guardianType.ProtocolId == cowType.ProtocolId &&
        guardianType.Identifier == "amethyst:guardian_cow" &&
        guardianType.MaxHealth == 50 && guardianType.Invulnerable,
        "Mythic-style entity aliases retain the vanilla network type");
    Check(!entityWorld.TrySpawn("item", 0, 70, 0, out _) &&
        !entityWorld.TrySpawn("not_a_real_mob", 0, 70, 0, out _) &&
        !entityWorld.TrySpawn("cow", double.NaN, 70, 0, out _) &&
        entityWorld.TrySpawn("cow", 2, 70, 3, out spawned) &&
        entityWorld.TrySpawn("guardian_cow", 3, 70, 4, out guardian) &&
        spawned.EntityId != guardian.EntityId &&
        spawned.ProtocolId == cowType.ProtocolId &&
        entityWorld.AllocateEntityId() > guardian.EntityId,
        "Entity instances validate spawn types and allocate collision-free IDs");
    Check(entityWorld.TryDamage(spawned.EntityId, 5, out var injured) &&
        injured.Health == 25 &&
        !entityWorld.TryDamage(guardian.EntityId, 1, out _) &&
        entityWorld.TryTeleport(spawned.EntityId, 5, 71, 6, 90, 0) &&
        entityWorld.Snapshot().Any(e => e.EntityId == spawned.EntityId &&
            e.X == 5 && e.Yaw == 90) &&
        entityWorld.TryRemove(spawned.EntityId) &&
        !entityWorld.TryRemove(spawned.EntityId),
        "Entity manager supports controlled damage, teleport, and removal");
    Check(host.RegisteredConsoleCommands.Contains("entityinfo") &&
        host.RegisteredConsoleCommands.Contains("entityspawn") &&
        await host.TryExecuteConsoleAsync("entityinfo cow", CancellationToken.None) &&
        await host.TryExecuteConsoleAsync("entityspawn cow 0 75 0", CancellationToken.None) &&
        entityWorld.Snapshot().Length == 2,
        "Entity Shard registers informational and spawn commands");
    IShardItemRegistry items = host.GetService<IShardItemRegistry>() ??
        throw new Exception("FAIL: Items Shard service not registered");
    Check(items.MinecraftVersion == "26.3" && items.Count == 1658 &&
        items.All.Count == 1658,
        "Items Shard auto-enabled and complete 26.3 definitions published");
    IShardRecipeRegistry recipes = host.GetService<IShardRecipeRegistry>() ??
        throw new Exception("FAIL: Items Shard recipe provider not registered");
    Check(recipes.Definitions.Select(d => d.Type).Distinct().Count() ==
        Enum.GetValues<ShardRecipeType>().Length,
        "All 22 Java 26.3 recipe serializers load from YAML");
    Check(recipes.Definitions.Any(d => d.Id == "probe_firework_rocket" &&
        !d.IsExecutable && d.Parameters is not null &&
        d.Parameters.ContainsKey("Shell") && d.Parameters.ContainsKey("Fuel")),
        "Special serializer configuration fields survive registration");
    Check(recipes.Definitions.Any(d => d.Id == "probe_decorated_pot" &&
        !d.IsExecutable) &&
        recipes.Definitions.Any(d => d.Id == "probe_smithing_trim" &&
        !d.IsExecutable),
        "Component-dependent recipes are registered but fail closed");
    Check(items.TryGetByIdentifier("stone", out var cutInput) &&
        items.TryGetByIdentifier("stone_bricks", out var cutResult) &&
        recipes.GetStonecuttingRecipes(cutInput.ProtocolId).Any(m =>
            m.RecipeId == "probe_stonecutting" &&
            m.Result.ItemId == cutResult.ProtocolId),
        "Stonecutting returns all matching result choices");
    Check(items.TryGetByIdentifier("beef", out var beef) &&
        recipes.TryGetCookingRecipe(ShardCookingStation.Campfire, beef.ProtocolId,
            out var campfire) && campfire.RecipeId == "probe_campfire" &&
        campfire.CookingTicks == 600,
        "Campfire cooking supports its own durations");
    Check(items.TryGetByIdentifier("netherite_upgrade_smithing_template", out var smithTemplate) &&
        items.TryGetByIdentifier("iron_sword", out var smithBase) &&
        items.TryGetByIdentifier("diamond", out var smithAddition) &&
        recipes.TryMatchSmithing(
            new ShardRecipeStack(smithTemplate.ProtocolId, 1),
            new ShardRecipeStack(smithBase.ProtocolId, 1),
            new ShardRecipeStack(smithAddition.ProtocolId, 1),
            out var smithMatch) &&
        smithMatch.RecipeId == "probe_smithing_transform" &&
        smithMatch.CopiesBaseComponents,
        "Smithing transform matches three distinct inputs");
    Check(items.TryGetByIdentifier("potion", out var potion) &&
        items.TryGetByIdentifier("nether_wart", out var wart) &&
        recipes.TryMatchBrewing(new ShardRecipeStack(potion.ProtocolId, 1)
        {
            Components = new Dictionary<string, string>
            {
                ["minecraft:potion_contents"] = """{"potion":"minecraft:water"}"""
            }
        }, new ShardRecipeStack(wart.ProtocolId, 1), out var brew) &&
        brew.RecipeId == "probe_brewing" &&
        brew.Result.Components is not null,
        "Brewing respects potion predicates and retains output components");
    var recipeGrid = new ShardRecipeStack[9];
    recipeGrid[4] = new ShardRecipeStack(55, 1); // dirt
    Check(recipes.RecipeCount >= 70 &&
        recipes.TryMatchCrafting(recipeGrid, out var definedRecipe) &&
        definedRecipe.RecipeId == "shovel_from_dirt" &&
        items.TryGetByIdentifier("iron_shovel", out var shovel) &&
        definedRecipe.Result == new ShardRecipeStack(shovel.ProtocolId, 1),
        "Items Shard loads user-defined YAML recipes before Gameplay enables");
    Check(host.RegisteredConsoleCommands.Contains("recipes") &&
        await host.TryExecuteConsoleAsync("recipes shovel", CancellationToken.None),
        "Recipe listing command is registered by the Items Shard");
    Check(items.TryGetById(405, out var table) &&
        table.Identifier == "minecraft:crafting_table" &&
        table.MaxStackSize == 64 && table.IsBlockItem &&
        table.Display == "Workshop Table" &&
        table.Lore is { Count: 1 } &&
        items.TryGetByIdentifier("crafting_table", out var byName) && byName == table,
        "Items can be resolved by protocol ID and canonical/short identifier");
    Check(items.TryGetByIdentifier("netherite_pickaxe", out var pickaxe) &&
        pickaxe.MaxStackSize == 1 &&
        items.TryGetByIdentifier("minecraft:ender_pearl", out var pearl) &&
        pearl.MaxStackSize == 16 &&
        items.IsValidStack(pickaxe.ProtocolId, 1) &&
        !items.IsValidStack(pickaxe.ProtocolId, 2) &&
        items.IsValidStack(pearl.ProtocolId, 16) &&
        !items.IsValidStack(pearl.ProtocolId, 17) &&
        !items.IsValidStack(1658, 1),
        "Known item stack caps and unknown-ID rejection come from the Items Shard");
    Check(host.RegisteredConsoleCommands.Contains("iteminfo") &&
        await host.TryExecuteConsoleAsync("iteminfo 405", CancellationToken.None),
        "iteminfo command is registered by the Items Shard");
    Check(host.Snapshot().Any(s => s.Id == "hello" && s.State == ShardState.Loaded),
        "example Shard installed but disabled");

    bool blocked = false;
    try { await host.DisableAsync("overworld"); }
    catch (InvalidOperationException) { blocked = true; }
    Check(blocked, "live dependency protects world provider from disabling");

    await host.EnableAsync("hello");
    Check(host.RegisteredConsoleCommands.Contains("hello"), "Shard console registration visible");
    Check(await host.TryExecuteConsoleAsync("hello Test", CancellationToken.None),
        "Shard console handler called");
    await host.DisableAsync("hello");
    Check(!await host.TryExecuteConsoleAsync("hello Test", CancellationToken.None),
        "Shard commands removed upon disable");

    bool itemsBlocked = false;
    try { await host.DisableAsync("items"); }
    catch (InvalidOperationException) { itemsBlocked = true; }
    Check(itemsBlocked, "Gameplay dependency protects the Items Shard from being disabled");

    bool entitiesBlocked = false;
    try { await host.DisableAsync("entities"); }
    catch (InvalidOperationException) { entitiesBlocked = true; }
    Check(entitiesBlocked, "Gameplay dependency protects entity runtime provider");

    await host.DisableAsync("gameplay");
    Check(host.GetService<IShardGameplay>() is null, "gameplay service removed");
    await host.DisableAsync("items");
    Check(host.GetService<IShardItemRegistry>() is null &&
        host.GetService<IShardRecipeRegistry>() is null &&
        host.GetService<IShardSkillEngine>() == skillEngine &&
        skillEngine.OwnerCount == 0 &&
        host.GetService<IShardItemDropSink>() is null &&
        !host.RegisteredConsoleCommands.Contains("skillinfo") &&
        !host.RegisteredConsoleCommands.Contains("recipes") &&
        !host.RegisteredConsoleCommands.Contains("iteminfo"),
        "Disabling Items Shard unregisters its definitions and iteminfo command");
    await host.DisableAsync("entities");
    Check(host.GetService<IShardEntityManager>() is null &&
        host.GetService<IShardEntityRegistry>() is null &&
        host.GetService<IShardEntityInteractions>() is null &&
        !host.RegisteredConsoleCommands.Contains("entityspawn"),
        "Disabling Entities Shard unregisters its services and commands");
    await host.DisableAsync("overworld");
    Check(host.GetService<IShardWorld>() is null, "world service removed");

    await host.EnableAsync("gameplay");
    Check(host.GetService<IShardWorld>() is not null &&
        host.GetService<IShardGameplay>() is not null &&
        host.GetService<IShardItemRegistry>() is not null &&
        host.GetService<IShardRecipeRegistry>() is not null &&
        host.GetService<IShardSkillEngine>() == skillEngine &&
        host.GetService<IShardItemDropSink>() is not null &&
        skillEngine.OwnerCount >= 10 &&
        host.GetService<IShardEntityManager>() is not null &&
        host.GetService<IShardEntityRegistry>() is not null &&
        host.GetService<IShardEntityInteractions>() is not null,
        "Enabling Gameplay automatically enables World, Items, Recipe and Entity providers");
}
finally
{
    try { Directory.Delete(temp, recursive: true); }
    catch (IOException) { }
}
Console.WriteLine("PASS: Shard lifecycle, dependency ordering, service API, console commands");
