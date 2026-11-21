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
    // Component node names are fixed in code, not loaded from a YAML mapping.
    string nodesFile = Path.Combine(temp, "shards", "items", "component-nodes.yml");
    // Test administrator YAML loaded through the real ShardHost data path.
    string yamlDir = Path.Combine(temp, "shards", "items", "definitions");
    Directory.CreateDirectory(yamlDir);
    File.WriteAllText(Path.Combine(yamlDir, "custom.yml"), """
        crafting_table:
          Display: "Workshop Table"
          Lore:
            - "Configured before the Shard loads"
          Components:
            ItemModel: amethyst:workshop_table
            Rarity: rare
            GlintOverride: true
            CustomModelData:
              floats: [2.5]
              flags: [true]
              strings: [amethyst:workshop]
              colors: [16711935]
            TooltipDisplay:
              hide_tooltip: false
              hidden_components: [minecraft:enchantments]
        diamond_pickaxe:
          Unbreakable: true
          ItemModel: amethyst:eternal_pickaxe
        stone:
          Options:
            CustomData: { WireHex: "0A00" }
            Glider: { Remove: true }
        cobblestone:
          Components:
            minecraft:use_effects: { Remove: true }
        obsidian:
          CustomName: "Fixed custom-name node"
        brick:
          StackSize: 16
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
        diamond_pickaxe:
          Skills:
            - 'dropItem{item=minecraft:diamond;chance=0} ~onLeftClick'
            - 'dropItem{item=minecraft:diamond;chance=0} ~onRightClick'
            - 'dropItem{item=minecraft:diamond;chance=0} ~onDrop'
            - 'dropItem{item=minecraft:diamond;chance=0} ~onStackDrop'
            - 'dropItem{item=minecraft:diamond;chance=0} ~onSwap'
            - 'dropItem{item=minecraft:diamond;chance=0} ~onPickup'
            - 'dropItem{item=minecraft:diamond;chance=0} ~onEquip'
            - 'dropItem{item=minecraft:diamond;chance=0} ~onUnequip'
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
    string[] itemEvents = [
        "onLeftClick", "onRightClick", "onDrop", "onStackDrop",
        "onSwap", "onPickup", "onEquip", "onUnequip"
    ];
    Check(skillEngine.TryGetSkills("minecraft:diamond_pickaxe", out var itemSkills) &&
        itemSkills.Count == itemEvents.Length &&
        itemEvents.All(evt => itemSkills.Any(skill =>
            string.Equals(skill.Trigger, evt, StringComparison.OrdinalIgnoreCase))) &&
        itemEvents.All(evt => skillEngine.DispatchAsync(
            new ShardSkillEvent("minecraft:diamond_pickaxe", evt)).GetAwaiter().GetResult()),
        "Items Shard registers all eight configurable event triggers through Core");
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
        items.All.Count == 1658 && items.ComponentNodes.Count == 45 &&
        items.ComponentNodes["minecraft:item_model"] == "ItemModel" &&
        items.ComponentNodes["minecraft:custom_data"] == "CustomData" &&
        items.ComponentNodes["minecraft:max_stack_size"] == "StackSize" &&
        !File.Exists(nodesFile),
        "Items Shard publishes only fixed component names without a YAML node config file");
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
    Check(ShardItemComponentIds.Count == 122 &&
        ShardItemComponentIds.All.Count == 122 &&
        ShardItemComponentIds.TryGetIdentifier(121, out var lastComponent) &&
        lastComponent == "minecraft:cushion/color" &&
        !items.ComponentNodes.ContainsKey("minecraft:cushion/color") &&
        !items.ComponentNodes.ContainsKey("minecraft:use_effects") &&
        !File.Exists(Path.Combine(root.FullName, "shards", "data",
            "items", "component-nodes.yml")),
        "122 registry IDs remain accessible but 77 unapproved YAML nodes stay unassigned");
    // Verify every explicitly approved spelling, including uncommon names.
    var expectedFixedNodes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["minecraft:custom_data"] = "CustomData",
        ["minecraft:max_stack_size"] = "StackSize",
        ["minecraft:max_damage"] = "DurabilityMax",
        ["minecraft:damage"] = "DurabilityDamage",
        ["minecraft:unbreakable"] = "Unbreakable",
        ["minecraft:custom_name"] = "CustomName",
        ["minecraft:minimum_attack_charge"] = "MinimumAttackCharge",
        ["minecraft:damage_type"] = "DamageType",
        ["minecraft:item_name"] = "ItemName",
        ["minecraft:item_model"] = "ItemModel",
        ["minecraft:lore"] = "Lore",
        ["minecraft:rarity"] = "Rarity",
        ["minecraft:enchantments"] = "Enchantments",
        ["minecraft:can_place_on"] = "CanPlaceOn",
        ["minecraft:can_break"] = "CanBreak",
        ["minecraft:attribute_modifiers"] = "AttributesModifiers",
        ["minecraft:custom_model_data"] = "CustomModelData",
        ["minecraft:tooltip_display"] = "TooltipDisplay",
        ["minecraft:repair_cost"] = "RepairCost",
        ["minecraft:creative_slot_lock"] = "CreativeSlotLock",
        ["minecraft:enchantment_glint_override"] = "GlintOverride",
        ["minecraft:food"] = "Food",
        ["minecraft:consumable"] = "Consumable",
        ["minecraft:use_remainder"] = "UseRemainder",
        ["minecraft:use_cooldown"] = "UseCooldown",
        ["minecraft:damage_resistant"] = "DamageResistant",
        ["minecraft:tool"] = "Tool",
        ["minecraft:weapon"] = "Weapon",
        ["minecraft:attack_range"] = "AttackRange",
        ["minecraft:enchantable"] = "Enchantable",
        ["minecraft:equippable"] = "Equippable",
        ["minecraft:repairable"] = "Repairable",
        ["minecraft:glider"] = "Glider",
        ["minecraft:tooltip_style"] = "TooltipStyle",
        ["minecraft:death_protection"] = "DeathProtection",
        ["minecraft:blocks_attacks"] = "BlocksAttacks",
        ["minecraft:piercing_weapon"] = "PiercingWeapon",
        ["minecraft:kinetic_weapon"] = "KineticWeapon",
        ["minecraft:dyed_color"] = "DyedColor",
        ["minecraft:instrument"] = "Instrument",
        ["minecraft:jukebox_playable"] = "JukeboxPlayable",
        ["minecraft:lock"] = "Lock",
        ["minecraft:compostable"] = "Compostable",
        ["minecraft:cooking_fuel"] = "CookingFuel",
        ["minecraft:brewing_fuel"] = "BrewingFuel",
    };
    Check(expectedFixedNodes.Count == 45 &&
        expectedFixedNodes.All(pair =>
            items.ComponentNodes.TryGetValue(pair.Key, out string? actual) &&
            actual == pair.Value) &&
        items.ComponentNodes.Count == expectedFixedNodes.Count,
        "All 45 requested fixed component node names match exactly");
    Check(items.TryGetByIdentifier("obsidian", out var namedObsidian) &&
        namedObsidian.Components is not null &&
        namedObsidian.Components.ContainsKey("minecraft:custom_name") &&
        items.TryGetByIdentifier("brick", out var limitedBrick) &&
        limitedBrick.MaxStackSize == 16 &&
        items.IsValidStack(limitedBrick.ProtocolId, 16) &&
        !items.IsValidStack(limitedBrick.ProtocolId, 17) &&
        items.TryGetByIdentifier("cobblestone", out var pendingCobbles) &&
        pendingCobbles.Components?.ContainsKey("minecraft:use_effects") == true,
        "CustomName and StackSize work; unassigned components require canonical minecraft: ID");

    Check(table.ItemModel == "amethyst:workshop_table" &&
        table.Components is { Count: 7 } &&
        table.Components.ContainsKey("minecraft:custom_name") &&
        table.Components.ContainsKey("minecraft:custom_model_data") &&
        ShardItemComponentIds.TryGetId("minecraft:item_model", out int modelComponent) &&
        modelComponent == 10 &&
        ShardItemComponentIds.TryGetId("minecraft:tooltip_display", out int tooltipComponent) &&
        tooltipComponent == 18,
        "YAML shorthands and Components expose version-pinned item patches");

    // Test actual protocol-777 slot bytes: definitions are not merely stored
    // as metadata; they must be sent to Java clients with correct field types.
    var slotWriter = new PacketWriter();
    InventoryProtocol.WriteStack(slotWriter, new ItemStack(table.ProtocolId, 1), items);
    var slot = new PacketReader(slotWriter.ToArray());
    Check(slot.ReadVarInt() == 1 && slot.ReadVarInt() == table.ProtocolId &&
        slot.ReadVarInt() == 7 && slot.ReadVarInt() == 0,
        "Configured item emits seven added components and zero removed");
    Check(slot.ReadVarInt() == 6 && slot.ReadByte() == 8 &&
        System.Text.Encoding.UTF8.GetString(slot.ReadFixedBytes(slot.ReadInt16())) ==
        "Workshop Table", "Display writes a real NBT custom_name component");
    Check(slot.ReadVarInt() == 10 &&
        slot.ReadString(256) == "amethyst:workshop_table",
        "item_model uses protocol-777 component ID and identifier codec");
    Check(slot.ReadVarInt() == 11 && slot.ReadVarInt() == 1 &&
        slot.ReadByte() == 8 &&
        System.Text.Encoding.UTF8.GetString(slot.ReadFixedBytes(slot.ReadInt16())) ==
        "Configured before the Shard loads",
        "Lore encodes as an array of NBT text components");
    Check(slot.ReadVarInt() == 12 && slot.ReadVarInt() == 2,
        "Rarity encodes as the correct protocol enum");
    Check(slot.ReadVarInt() == 17 && slot.ReadVarInt() == 1 &&
        Math.Abs(slot.ReadFloat() - 2.5f) < 0.001 &&
        slot.ReadVarInt() == 1 && slot.ReadByte() == 1 &&
        slot.ReadVarInt() == 1 && slot.ReadString(256) == "amethyst:workshop" &&
        slot.ReadVarInt() == 1 && slot.ReadInt32() == 16711935,
        "Custom model data uses the modern floats/flags/strings/colors payload");
    Check(slot.ReadVarInt() == 18 && slot.ReadByte() == 0 &&
        slot.ReadVarInt() == 1 && slot.ReadVarInt() == 13,
        "Tooltip display uses validated component registry references");
    Check(slot.ReadVarInt() == 21 && slot.ReadByte() == 1 &&
        slot.Remaining == 0, "Glint override encodes as a boolean component");

    Check(items.TryGetByIdentifier("stone", out var stone) &&
        stone.Components is { Count: 2 } &&
        stone.Components.ContainsKey("minecraft:custom_data") &&
        stone.Components.ContainsKey("minecraft:glider"),
        "Fixed YAML node names map Options values to canonical components");
    var blobWriter = new PacketWriter();
    InventoryProtocol.WriteStack(blobWriter, new ItemStack(stone.ProtocolId, 1), items);
    var blobReader = new PacketReader(blobWriter.ToArray());
    Check(blobReader.ReadVarInt() == 1 &&
        blobReader.ReadVarInt() == stone.ProtocolId &&
        blobReader.ReadVarInt() == 1 && blobReader.ReadVarInt() == 1 &&
        blobReader.ReadVarInt() == 0 &&
        blobReader.ReadByte() == 0x0A && blobReader.ReadByte() == 0 &&
        blobReader.ReadVarInt() == 34 && blobReader.Remaining == 0,
        "Any registered component supports raw wire payloads and removal patches");

    Check(items.TryGetByIdentifier("diamond_pickaxe", out var configuredPickaxe) &&
        configuredPickaxe.ItemModel == "amethyst:eternal_pickaxe",
        "Configuration applies to vanilla damageable item definitions");
    var durableWriter = new PacketWriter();
    InventoryProtocol.WriteStack(durableWriter,
        new ItemStack(configuredPickaxe.ProtocolId, 1)
        { Damage = 11, Unbreaking = 3, Mending = true, RepairCost = 4 }, items);
    var durable = new PacketReader(durableWriter.ToArray());
    Check(durable.ReadVarInt() == 1 &&
        durable.ReadVarInt() == configuredPickaxe.ProtocolId &&
        durable.ReadVarInt() == 5 && durable.ReadVarInt() == 0 &&
        durable.ReadVarInt() == 3 && durable.ReadVarInt() == 11 &&
        durable.ReadVarInt() == 4 &&
        durable.ReadVarInt() == 10 &&
        durable.ReadString(256) == "amethyst:eternal_pickaxe" &&
        durable.ReadVarInt() == 13 && durable.ReadVarInt() == 2 &&
        durable.ReadVarInt() == VanillaRegistries.IndexOf(
            "minecraft:enchantment", "minecraft:unbreaking") &&
        durable.ReadVarInt() == 3 &&
        durable.ReadVarInt() == VanillaRegistries.IndexOf(
            "minecraft:enchantment", "minecraft:mending") &&
        durable.ReadVarInt() == 1 &&
        durable.ReadVarInt() == 19 && durable.ReadVarInt() == 4 &&
        durable.Remaining == 0,
        "Definition patches coexist with authoritative durability/enchantment components");

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
    Check(host.RegisteredConsoleCommands.Contains("componentnodes") &&
        await host.TryExecuteConsoleAsync("componentnodes model", CancellationToken.None),
        "componentnodes console command lists fixed names and pending registry IDs");
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
        !host.RegisteredConsoleCommands.Contains("iteminfo") &&
        !host.RegisteredConsoleCommands.Contains("componentnodes"),
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
        skillEngine.TryGetSkills("diamond_pickaxe", out var reloadedItemSkills) &&
        reloadedItemSkills.Count == 8 &&
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
