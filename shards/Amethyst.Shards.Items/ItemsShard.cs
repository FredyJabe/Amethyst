using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Amethyst.Shards;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Amethyst.BuiltinShards;

/// <summary>Validated, version-pinned vanilla definitions plus Mythic-style
/// YAML overrides. Each YAML root key is an item ID; an optional Id field
/// resolves it to a known 26.3 vanilla material. Custom aliases cannot add
/// protocol registry entries or create distinguishable item stacks.</summary>
public sealed class ItemCatalog : IShardItemRegistry
{
    private static readonly Regex ItemKey = new(
        @"^([a-z0-9_.-]+:)?[a-z0-9_./-]+$", RegexOptions.Compiled |
        RegexOptions.CultureInvariant);
    private readonly ShardItemDefinition[] _byId;
    private readonly ItemComponentNodeMap _componentNodes;

    public IReadOnlyDictionary<string, string> ComponentNodes => _componentNodes.Nodes;
    private readonly IReadOnlyList<ShardItemDefinition> _all;
    private readonly Dictionary<string, ShardItemDefinition> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    public string MinecraftVersion => "26.3";
    public int Count => _byId.Length;
    public IReadOnlyList<ShardItemDefinition> All => _all;

    public ItemCatalog(string vanillaYamlPath, string? overridesDirectory = null,
        ItemComponentNodeMap? componentNodes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vanillaYamlPath);
        _componentNodes = componentNodes ?? new ItemComponentNodeMap();
        var definitions = new List<ShardItemDefinition>();
        foreach (var (root, options) in ReadEntries(vanillaYamlPath, _componentNodes))
        {
            if (options.ProtocolId != definitions.Count ||
                options.Id is null || options.MaxStackSize is null ||
                options.BlockItem is null)
                throw new InvalidDataException(
                    $"Invalid or out-of-order vanilla item '{root}' in {vanillaYamlPath}.");
            string key = Normalize(options.Id);
            if (!key.StartsWith("minecraft:", StringComparison.Ordinal) ||
                Normalize(root) != key["minecraft:".Length..])
                throw new InvalidDataException(
                    $"Vanilla item root '{root}' must match its Id '{options.Id}'.");
            int max = options.MaxStackSize.Value;
            if (max is not (1 or 16 or 64))
                throw new InvalidDataException($"Invalid 26.3 max stack size for {root}.");
            int maxDurability = options.MaxDurability ?? 0;
            if (maxDurability is < 0 or > 32767 ||
                (maxDurability > 0 && max != 1))
                throw new InvalidDataException($"Invalid vanilla durability for {root}.");
            var item = new ShardItemDefinition(options.ProtocolId.Value, key,
                max, options.BlockItem.Value) { MaxDurability = maxDurability, Stats = options.Stats };
            if (_byName.ContainsKey(key) || _byName.ContainsKey(root))
                throw new InvalidDataException($"Duplicate vanilla item {key}.");
            definitions.Add(item);
            _vanillaMax[item.ProtocolId] = max;
            _byName.Add(key, item);
            _byName.Add(Normalize(root), item);
        }
        if (definitions.Count != 1658 || definitions[0].Identifier != "minecraft:air" ||
            definitions[^1].ProtocolId != 1657)
            throw new InvalidDataException("Wrong Minecraft 26.3 item count or version.");
        _byId = definitions.ToArray();

        if (overridesDirectory is not null)
        {
            Directory.CreateDirectory(overridesDirectory);
            string[] files = Directory.EnumerateFiles(overridesDirectory, "*",
                SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).ToArray();
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
                foreach (var (root, options) in ReadEntries(file, _componentNodes))
                    ApplyOverride(root, options, file, touched);
        }
        // Capture a read-only snapshot after YAML overrides have been applied.
        _all = new ReadOnlyCollection<ShardItemDefinition>(_byId);
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private void ApplyOverride(string root, ItemFields fields, string file,
        HashSet<string> touched)
    {
        string key = Normalize(root);
        if (!ItemKey.IsMatch(key))
            throw new InvalidDataException($"Invalid root item ID '{root}' in {file}.");
        if (!touched.Add(key))
            throw new InvalidDataException($"Item ID '{root}' was configured twice in user YAML files.");
        // Existing protocol IDs and vanilla names refer to the same canonical
        // entry. Arbitrary aliases require Id: <vanilla material>, like Mythic.
        bool canonical = _byName.TryGetValue(key, out ShardItemDefinition original);
        if (!canonical && int.TryParse(key, NumberStyles.None,
                CultureInfo.InvariantCulture, out int numeric))
            canonical = TryGetById(numeric, out original);
        if (canonical)
        {
            // Numeric and namespaced roots can refer to the same vanilla
            // item; report duplicate overrides instead of last-write-wins.
            if (!touched.Add("protocol:" + original.ProtocolId.ToString(
                    CultureInfo.InvariantCulture)))
                throw new InvalidDataException(
                    $"Vanilla item '{original.Identifier}' was configured more than once.");
            if (fields.Id is not null && !ResolvesTo(fields.Id, original.ProtocolId))
                throw new InvalidDataException($"'{root}' in {file} cannot change its vanilla Id.");
            if (fields.ProtocolId is int supplied && supplied != original.ProtocolId)
                throw new InvalidDataException($"'{root}' in {file} cannot change ProtocolId.");
            if (fields.MaxDurability is int changedDurability &&
                changedDurability != original.MaxDurability)
                throw new InvalidDataException(
                    $"'{root}' in {file} cannot change vanilla MaxDurability.");
            int? componentMax = fields.Components is not null &&
                fields.Components.TryGetValue("minecraft:max_stack_size", out string? encodedMax)
                ? System.Text.Json.JsonSerializer.Deserialize<int>(encodedMax) : null;
            if (componentMax.HasValue && fields.MaxStackSize.HasValue)
                throw new InvalidDataException(
                    $"'{root}' cannot specify both MaxStackSize and Components.max_stack_size.");
            int max = componentMax ?? fields.MaxStackSize ?? original.MaxStackSize;
            if (max < 1 || max > VanillaMax(original.ProtocolId))
                throw new InvalidDataException(
                    $"'{root}' in {file} has MaxStackSize outside vanilla capacity.");
            ShardItemDefinition updated = ApplyMetadata(original with
            {
                MaxStackSize = max,
                IsBlockItem = fields.BlockItem ?? original.IsBlockItem
            }, fields);
            _byId[original.ProtocolId] = updated;
            _byName[updated.Identifier] = updated;
            _byName[updated.Identifier["minecraft:".Length..]] = updated;
            return;
        }

        // A Mythic-style custom name refers to an existing base material.
        // The alias is queryable as an item definition but does NOT create a
        // new client-visible item ID or a per-stack identity.
        if (fields.Id is null || !TryResolve(fields.Id, out ShardItemDefinition material))
            throw new InvalidDataException(
                $"Custom item '{root}' in {file} needs a valid vanilla 'Id'.");
        if (key.StartsWith("minecraft:", StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Cannot register unknown minecraft: item '{root}' in {file}.");
        if (fields.ProtocolId is int requested && requested != material.ProtocolId)
            throw new InvalidDataException(
                $"Custom item '{root}' cannot assign a new protocol ID.");
        if (fields.Components is not null &&
                fields.Components.TryGetValue("minecraft:max_stack_size", out string? aliasJson) &&
                System.Text.Json.JsonSerializer.Deserialize<int>(aliasJson) != material.MaxStackSize ||
            fields.MaxStackSize is int aliasMax && aliasMax != material.MaxStackSize ||
            fields.MaxDurability is int aliasDurability &&
                aliasDurability != material.MaxDurability ||
            fields.BlockItem is bool block && block != material.IsBlockItem)
            throw new InvalidDataException(
                $"Custom alias '{root}' cannot override physical stack or block properties; " +
                "item stacks do not yet carry an alias identity.");
        string aliasId = key.Contains(':') ? key : "amethyst:" + key;
        if (_byName.ContainsKey(aliasId))
            throw new InvalidDataException(
                $"Custom alias '{aliasId}' was defined twice in YAML files.");
        var alias = ApplyMetadata(material with { Identifier = aliasId }, fields);
        _byName.Add(aliasId, alias);
        if (aliasId != key) _byName.Add(key, alias);
    }

    private int VanillaMax(int id) => _vanillaMax[id];
    private readonly int[] _vanillaMax = new int[1658];

    private bool ResolvesTo(string id, int expected) =>
        TryResolve(id, out var item) && item.ProtocolId == expected;

    private bool TryResolve(string id, out ShardItemDefinition item)
    {
        string key = Normalize(id);
        if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture,
            out int numeric)) return TryGetById(numeric, out item);
        return TryGetByIdentifier(key, out item);
    }

    private static ShardItemDefinition ApplyMetadata(ShardItemDefinition item, ItemFields fields)
    {
        // Merge the immutable definition patch rather than replacing unrelated
        // components when one YAML override sets only a single property.
        var patch = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.Components is not null)
            foreach (var (key, value) in item.Components)
                patch[key] = value;
        if (fields.Components is not null)
            foreach (var (key, value) in fields.Components)
                patch[key] = value;

        void Shorthand(string key, object value)
        {
            if (fields.Components?.ContainsKey(key) == true)
                throw new InvalidDataException(
                    $"Component '{key}' cannot appear in both top-level fields and Components.");
            patch[key] = System.Text.Json.JsonSerializer.Serialize(value);
        }

        if (fields.Display is not null)
            Shorthand("minecraft:custom_name", fields.Display);
        if (fields.ItemName is not null)
            Shorthand("minecraft:item_name", fields.ItemName);
        if (fields.Lore is not null)
            Shorthand("minecraft:lore", fields.Lore);
        if (fields.ItemModel is not null)
            Shorthand("minecraft:item_model", fields.ItemModel);
        if (fields.Rarity is not null)
            Shorthand("minecraft:rarity", fields.Rarity);
        if (fields.TooltipStyle is not null)
            Shorthand("minecraft:tooltip_style", fields.TooltipStyle);
        if (fields.Glint is bool glint)
            Shorthand("minecraft:enchantment_glint_override", glint);
        if (fields.Unbreakable is bool unbreakable)
            Shorthand("minecraft:unbreakable", unbreakable);
        if (fields.CustomModelData is int customModel &&
            fields.Components?.ContainsKey("minecraft:custom_model_data") != true)
        {
            patch["minecraft:custom_model_data"] =
                ItemComponentConfig.Encode("minecraft:custom_model_data",
                    new YamlScalarNode(customModel.ToString(CultureInfo.InvariantCulture)),
                    item.Identifier);
        }
        if (fields.MaxStackSize is int configuredStack)
            Shorthand("minecraft:max_stack_size", configuredStack);

        string? model = fields.ItemModel ?? item.ItemModel;
        if (fields.Components is not null &&
            fields.Components.TryGetValue("minecraft:item_model", out string? encodedModel))
            model = System.Text.Json.JsonSerializer.Deserialize<string>(encodedModel);
        return item with
        {
            Display = fields.Display ?? item.Display,
            ItemName = fields.ItemName ?? item.ItemName,
            ItemModel = model,
            Lore = fields.Lore is null ? item.Lore : Array.AsReadOnly(fields.Lore),
            CustomModelData = fields.CustomModelData ?? item.CustomModelData,
            Hidden = fields.Hidden ?? item.Hidden,
            Stats = fields.Stats ?? item.Stats,
            Components = new ReadOnlyDictionary<string, string>(patch)
        };
    }

    public bool TryGetById(int protocolId, out ShardItemDefinition definition)
    {
        if ((uint)protocolId < (uint)_byId.Length)
        {
            definition = _byId[protocolId];
            return true;
        }
        definition = default;
        return false;
    }

    public bool TryGetByIdentifier(string identifier, out ShardItemDefinition definition)
    {
        definition = default;
        if (string.IsNullOrWhiteSpace(identifier)) return false;
        string key = Normalize(identifier);
        return _byName.TryGetValue(key, out definition);
    }

    public bool IsValidStack(int protocolId, int count) =>
        protocolId == 0 ? count == 0 :
        count > 0 && TryGetById(protocolId, out var item) &&
        count <= item.MaxStackSize;

    private sealed class ItemFields
    {
        public string? Id;
        public int? ProtocolId;
        public int? MaxStackSize;
        public int? MaxDurability;
        public bool? BlockItem;
        public string? Display;
        public string? ItemName;
        public string[]? Lore;
        public int? CustomModelData;
        public bool? Hidden;
        public string? ItemModel;
        public string? Rarity;
        public string? TooltipStyle;
        public bool? Glint;
        public bool? Unbreakable;
        public IReadOnlyDictionary<string, string>? Components;
        public IReadOnlyList<ShardStatModifier>? Stats;
    }

    private static IEnumerable<(string Key, ItemFields Fields)> ReadEntries(
        string path, ItemComponentNodeMap componentNodes)
    {
        var yaml = new YamlStream();
        try
        {
            using var reader = File.OpenText(path);
            yaml.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"Malformed items YAML '{path}': {ex.Message}", ex);
        }
        if (yaml.Documents.Count == 0) yield break; // comment-only example file
        if (yaml.Documents.Count != 1 ||
            yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException($"Items file '{path}' must be a root YAML mapping.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root.Children)
        {
            if (entry.Key is not YamlScalarNode keyNode || keyNode.Value is not { } key ||
                !keys.Add(Normalize(key)))
                throw new InvalidDataException($"Duplicate or invalid root key in {path}.");
            if (entry.Value is not YamlMappingNode node)
                throw new InvalidDataException($"Item '{key}' in {path} must have a mapping of properties.");
            var fields = new ItemFields();
            var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddComponent(string yamlNode, YamlNode data)
            {
                if (!componentNodes.TryResolve(yamlNode, out string component))
                    throw new InvalidDataException(
                        $"Unknown item component '{yamlNode}' in '{key}' ({path}).");
                var patch = new Dictionary<string, string>(StringComparer.Ordinal);
                if (fields.Components is not null)
                    foreach (var (previous, value) in fields.Components)
                        patch.Add(previous, value);
                if (!patch.TryAdd(component,
                    ItemComponentConfig.Encode(component, data, $"{key} ({path})")))
                    throw new InvalidDataException(
                        $"Duplicate component {component} in '{key}' ({path}).");
                fields.Components = patch;
            }
            foreach (var field in node.Children)
            {
                string fieldName = Scalar(field.Key, path, key);
                if (!properties.Add(fieldName))
                    throw new InvalidDataException($"Duplicate '{fieldName}' in item '{key}' ({path}).");
                switch (fieldName.ToLowerInvariant())
                {
                    case "id": fields.Id = Scalar(field.Value, path, key); break;
                    case "protocolid": fields.ProtocolId = Integer(field.Value, path, key); break;
                    case "maxdurability":
                        fields.MaxDurability = Integer(field.Value, path, key);
                        break;
                    case "maxstacksize":
                        if (fields.MaxStackSize.HasValue)
                            throw new InvalidDataException($"Specify MaxStackSize / Options.StackSize only once in {key}.");
                        fields.MaxStackSize = Integer(field.Value, path, key);
                        break;
                    case "display": fields.Display = LimitedString(field.Value, path, key); break;
                    case "itemname": fields.ItemName = LimitedString(field.Value, path, key); break;
                    case "itemmodel":
                    case "item_model":
                        fields.ItemModel = ItemComponentConfig.Identifier(field.Value, key);
                        break;
                    case "rarity":
                        fields.Rarity = System.Text.Json.JsonSerializer.Deserialize<string>(
                            ItemComponentConfig.Encode("minecraft:rarity", field.Value, key));
                        break;
                    case "tooltipstyle":
                        fields.TooltipStyle = ItemComponentConfig.Identifier(field.Value, key);
                        break;
                    case "glint":
                        fields.Glint = Boolean(field.Value, path, key);
                        break;
                    case "unbreakable":
                        fields.Unbreakable = Boolean(field.Value, path, key);
                        break;
                    case "stats":
                        fields.Stats = StatModifiers.Parse(field.Value, key);
                        break;
                    case "components":
                        foreach (var (component, json) in
                            ItemComponentConfig.Parse(field.Value, key, componentNodes))
                        {
                            var patch = new Dictionary<string, string>(StringComparer.Ordinal);
                            if (fields.Components is not null)
                                foreach (var (previous, value) in fields.Components)
                                    patch.Add(previous, value);
                            if (!patch.TryAdd(component, json))
                                throw new InvalidDataException(
                                    $"Duplicate component {component} in '{key}' ({path}).");
                            fields.Components = patch;
                        }
                        break;
                    case "custommodeldata":
                        // The approved fixed node supports both the legacy
                        // integer shorthand and modern structured model data.
                        if (field.Value is YamlScalarNode)
                        {
                            fields.CustomModelData = Integer(field.Value, path, key);
                            if (fields.CustomModelData < 0)
                                throw new InvalidDataException(
                                    $"Negative CustomModelData in {key} ({path}).");
                        }
                        AddComponent(fieldName, field.Value);
                        break;
                    case "hidden": fields.Hidden = Boolean(field.Value, path, key); break;
                    case "lore":
                        if (field.Value is not YamlSequenceNode lines || lines.Children.Count > 64)
                            throw new InvalidDataException($"Lore in '{key}' must be a sequence (max 64).");
                        fields.Lore = lines.Children.Select(x => LimitedString(x, path, key)).ToArray();
                        break;
                    case "options":
                        if (field.Value is not YamlMappingNode opts)
                            throw new InvalidDataException($"Options in '{key}' must be a mapping.");
                        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var option in opts.Children)
                        {
                            string optionName = Scalar(option.Key, path, key);
                            if (!names.Add(optionName))
                                throw new InvalidDataException($"Duplicate option '{optionName}' in {key}.");
                            switch (optionName.ToLowerInvariant())
                            {
                                case "blockitem":
                                    fields.BlockItem = Boolean(option.Value, path, key);
                                    break;
                                case "stacksize":
                                    if (fields.MaxStackSize.HasValue)
                                        throw new InvalidDataException(
                                            $"Specify MaxStackSize / Options.StackSize only once in {key}.");
                                    fields.MaxStackSize = Integer(option.Value, path, key);
                                    break;
                                case "hidden":
                                    if (fields.Hidden.HasValue)
                                        throw new InvalidDataException($"Specify Hidden once in {key}.");
                                    fields.Hidden = Boolean(option.Value, path, key);
                                    break;
                                default:
                                    if (componentNodes.TryResolve(optionName, out _))
                                        AddComponent(optionName, option.Value);
                                    else
                                        throw new InvalidDataException(
                                            $"Unsupported Options.{optionName} in '{key}' ({path}).");
                                    break;
                            }
                        }
                        break;
                    default:
                        if (componentNodes.TryResolve(fieldName, out _))
                            AddComponent(fieldName, field.Value);
                        else
                            throw new InvalidDataException(
                                $"Unsupported item property '{fieldName}' in '{key}' ({path}).");
                        break;
                }
            }
            yield return (key, fields);
        }
    }

    private static string Scalar(YamlNode node, string file, string key) =>
        node is YamlScalarNode { Value: { } value } ? value :
            throw new InvalidDataException($"Expected scalar in item '{key}' ({file}).");

    private static string LimitedString(YamlNode node, string file, string key)
    {
        string s = Scalar(node, file, key);
        if (s.Length > 1024)
            throw new InvalidDataException($"Text too long in item '{key}' ({file}).");
        return s;
    }

    private static int Integer(YamlNode node, string file, string key) =>
        int.TryParse(Scalar(node, file, key), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int i) ? i :
            throw new InvalidDataException($"Invalid number in item '{key}' ({file}).");

    private static bool Boolean(YamlNode node, string file, string key) =>
        bool.TryParse(Scalar(node, file, key), out bool value) ? value :
            throw new InvalidDataException($"Invalid boolean in item '{key}' ({file}).");
}

public sealed class ItemsShard : IShard
{
    private IShardContext? _context;
    public Task OnLoadAsync(IShardContext context, CancellationToken ct)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task OnEnableAsync(CancellationToken ct)
    {
        IShardContext ctx = _context ?? throw new InvalidOperationException("Items Shard is not loaded.");
        string directory = Path.GetDirectoryName(typeof(ItemsShard).Assembly.Location)
            ?? throw new InvalidOperationException("Cannot locate Items Shard assembly.");
        string definitions = Path.Combine(ctx.DataDirectory, "definitions");
        Directory.CreateDirectory(definitions);
        string example = Path.Combine(definitions, "example-items.yml");
        if (!File.Exists(example))
            File.WriteAllText(example, """
                # MythicMobs-style item overrides. Uncomment examples to enable.
                # Each root key is an item ID; Id is the base vanilla material.
                # Components are Java 26.3 item-stack patches sent to clients.
                # Client-side models require a matching resource pack.
                # diamond_pickaxe:
                #   Id: minecraft:diamond_pickaxe
                #   Display: "<aqua>Miner's Pickaxe</aqua>"
                #   ItemModel: amethyst:miners_pickaxe
                #   Stats:
                #     mining_speed:
                #       Trigger: onMine
                #       Base: 1.5
                #       Operation: add
                #   Components:
                #     Rarity: rare
                #     GlintOverride: true
                #     CustomModelData:
                #       floats: [3.0]
                #       flags: []
                #       strings: [amethyst:miners_pickaxe]
                #       colors: []
                #   Lore:
                #     - "Configured in the Items Shard"
                #   Options:
                #     StackSize: 1
                #     BlockItem: false
                #
                # legendary_pickaxe:
                #   Id: diamond_pickaxe
                #   Display: "<gold>Legendary Pickaxe</gold>"
                #   Lore:
                #     - "An alias of the vanilla pickaxe; NOT a new protocol item."
                """ + Environment.NewLine);
        // Component YAML node assignments are fixed by Amethyst source code.
        // No component-nodes.yml is generated, read or monitored.
        var componentNodes = new ItemComponentNodeMap();
        var catalog = new ItemCatalog(Path.Combine(directory, "items-26.3.yml"),
            definitions, componentNodes);
        string recipeDirectory = Path.Combine(ctx.DataDirectory, "recipes");
        Directory.CreateDirectory(recipeDirectory);
        string exampleRecipe = Path.Combine(recipeDirectory, "example-recipes.yml");
        if (!File.Exists(exampleRecipe))
            File.WriteAllText(exampleRecipe, """
                # Each YAML root key is a recipe ID. Uncomment to enable.
                # custom_torches:
                #   Type: Shapeless
                #   Ingredients:
                #     - minecraft:coal
                #     - minecraft:stick
                #   Result: { Id: minecraft:torch, Amount: 8 }
                #
                # alternative_furnace:
                #   Type: Furnace
                #   Ingredient: minecraft:dirt
                #   Result: { Id: minecraft:clay, Amount: 1 }
                #   CookingTime: 100
                #
                # To disable an included recipe by its root ID:
                # furnace:
                #   Enabled: false
                """ + Environment.NewLine);
        var recipes = new RecipeCatalog(catalog,
            Path.Combine(directory, "recipes-26.3.yml"), recipeDirectory);
        ctx.RegisterService<IShardItemRegistry>(catalog);
        ctx.RegisterService<IShardRecipeRegistry>(recipes);
        string enchantmentDirectory = Path.Combine(ctx.DataDirectory, "enchantments");
        Directory.CreateDirectory(enchantmentDirectory);
        string exampleEnchantment = Path.Combine(enchantmentDirectory, "example-enchantments.yml");
        if (!File.Exists(exampleEnchantment))
            File.WriteAllText(exampleEnchantment, """
                # Copy an enchantment key from the bundled enchantments-26.3.yml.
                # Only fields provided here replace bundled values.
                # sharpness:
                #   MaxLevel: 6
                #   Stats:
                #     damage:
                #       Trigger: onAttack
                #       Base: 0.5
                #       PerLevel: 0.75
                #       Conditions: ["level{min=1}"]
                """ + Environment.NewLine);
        var enchantments = new EnchantmentCatalog(
            Path.Combine(directory, "enchantments-26.3.yml"), enchantmentDirectory);
        EnchantmentSkills.Register(ctx);
        // Fail fast for unknown/invalid Stats conditions instead of throwing
        // only after a player triggers an affected event.
        var skillEngine = ctx.GetRequiredService<IShardSkillEngine>();
        foreach (var stat in catalog.All.SelectMany(item => item.Stats ?? []))
            skillEngine.ValidateConditions(stat.Conditions);
        foreach (var stat in enchantments.All.SelectMany(e => e.Stats))
            skillEngine.ValidateConditions(stat.Conditions);
        ctx.RegisterSkillInstructions(enchantments.Instructions);
        ctx.RegisterService<IShardEnchantmentRegistry>(enchantments);
        ctx.RegisterConsoleCommand("enchantments", (args, token) =>
        {
            string filter = args.Trim();
            var found = enchantments.All.Where(e => filter.Length == 0 ||
                e.Identifier.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(43).Select(e => $"{e.Identifier} (I..{e.MaxLevel})");
            ctx.Log($"Enchantments ({enchantments.Count}): " + string.Join(", ", found));
            return Task.CompletedTask;
        });
        ctx.RegisterConsoleCommand("iteminfo", (args, token) =>
        {
            string query = args.Trim();
            bool found = int.TryParse(query, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int protocolId)
                ? catalog.TryGetById(protocolId, out ShardItemDefinition byId)
                    && Report(byId)
                : catalog.TryGetByIdentifier(query, out ShardItemDefinition byName)
                    && Report(byName);
            if (!found) ctx.Log("Usage: iteminfo <item-id|minecraft:item_name> (known configured item)");
            return Task.CompletedTask;

            bool Report(ShardItemDefinition item)
            {
                ctx.Log($"{item.Identifier} | protocol ID {item.ProtocolId} | " +
                    $"max stack {item.MaxStackSize} | max durability: {item.MaxDurability} | block item: {item.IsBlockItem}" +
                    (item.Display is null ? "" : $" | display: {item.Display}") +
                    (item.ItemModel is null ? "" : $" | model: {item.ItemModel}") +
                    $" | components: {item.Components?.Count ?? 0} | stats: {item.Stats?.Count ?? 0}");
                return true;
            }
        });
        ctx.RegisterConsoleCommand("componentnodes", (args, token) =>
        {
            string filter = args.Trim();
            int count = 0;
            for (int i = 0; i < ShardItemComponentIds.Count; i++)
            {
                string component = ShardItemComponentIds.All[i];
                string? node = componentNodes.Nodes.TryGetValue(component, out string? assigned)
                    ? assigned : null;
                if (filter.Length > 0 &&
                    !component.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !(node?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    filter != i.ToString(CultureInfo.InvariantCulture))
                    continue;
                ctx.Log($"[{i,3}] {component} => {node ?? "(unassigned)"}");
                count++;
            }
            ctx.Log($"Component YAML nodes: {count} matching of " +
                $"{ShardItemComponentIds.Count} registered in Java {catalog.MinecraftVersion}; " +
                $"{componentNodes.Nodes.Count} fixed assignments, " +
                $"{componentNodes.PendingCount} pending (no user-configurable mapping file).");
            return Task.CompletedTask;
        });
        ctx.RegisterConsoleCommand("recipes", (args, token) =>
        {
            string filter = args.Trim();
            string[] ids = recipes.RecipeIds
                .Where(id => filter.Length == 0 ||
                    id.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(30).ToArray();
            ctx.Log($"Recipes ({recipes.RecipeCount} enabled): " +
                (ids.Length == 0 ? "no matches" : string.Join(", ", ids)) +
                (ids.Length == 30 ? " … (showing first 30)" : ""));
            return Task.CompletedTask;
        });
        ctx.Log($"Registered {catalog.Count} vanilla Java {catalog.MinecraftVersion} " +
            $"item definitions and {recipes.RecipeCount} YAML recipes; " +
            $"item overrides: {definitions}; fixed component nodes: " +
            $"{componentNodes.Nodes.Count} assigned, {componentNodes.PendingCount} pending; " +
            $"recipe overrides: {recipeDirectory}; " +
            $"enchantments: {enchantments.Count} (overrides: {enchantmentDirectory}).");
        return Task.CompletedTask;
    }

    public Task OnDisableAsync(CancellationToken ct) => Task.CompletedTask;
}
