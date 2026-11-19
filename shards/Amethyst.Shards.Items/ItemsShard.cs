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
    private readonly IReadOnlyList<ShardItemDefinition> _all;
    private readonly Dictionary<string, ShardItemDefinition> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    public string MinecraftVersion => "26.3";
    public int Count => _byId.Length;
    public IReadOnlyList<ShardItemDefinition> All => _all;

    public ItemCatalog(string vanillaYamlPath, string? overridesDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vanillaYamlPath);
        var definitions = new List<ShardItemDefinition>();
        foreach (var (root, options) in ReadEntries(vanillaYamlPath))
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
            var item = new ShardItemDefinition(options.ProtocolId.Value, key,
                max, options.BlockItem.Value);
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
                foreach (var (root, options) in ReadEntries(file))
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
            int max = fields.MaxStackSize ?? original.MaxStackSize;
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
        if (fields.MaxStackSize is int aliasMax && aliasMax != material.MaxStackSize ||
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

    private static ShardItemDefinition ApplyMetadata(ShardItemDefinition item, ItemFields fields) =>
        item with
        {
            Display = fields.Display ?? item.Display,
            ItemName = fields.ItemName ?? item.ItemName,
            Lore = fields.Lore is null ? item.Lore : Array.AsReadOnly(fields.Lore),
            CustomModelData = fields.CustomModelData ?? item.CustomModelData,
            Hidden = fields.Hidden ?? item.Hidden
        };

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
        public bool? BlockItem;
        public string? Display;
        public string? ItemName;
        public string[]? Lore;
        public int? CustomModelData;
        public bool? Hidden;
    }

    private static IEnumerable<(string Key, ItemFields Fields)> ReadEntries(string path)
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
            foreach (var field in node.Children)
            {
                string fieldName = Scalar(field.Key, path, key);
                if (!properties.Add(fieldName))
                    throw new InvalidDataException($"Duplicate '{fieldName}' in item '{key}' ({path}).");
                switch (fieldName.ToLowerInvariant())
                {
                    case "id": fields.Id = Scalar(field.Value, path, key); break;
                    case "protocolid": fields.ProtocolId = Integer(field.Value, path, key); break;
                    case "maxstacksize":
                        if (fields.MaxStackSize.HasValue)
                            throw new InvalidDataException($"Specify MaxStackSize / Options.StackSize only once in {key}.");
                        fields.MaxStackSize = Integer(field.Value, path, key);
                        break;
                    case "display": fields.Display = LimitedString(field.Value, path, key); break;
                    case "itemname": fields.ItemName = LimitedString(field.Value, path, key); break;
                    case "custommodeldata":
                        fields.CustomModelData = Integer(field.Value, path, key);
                        if (fields.CustomModelData < 0)
                            throw new InvalidDataException($"Negative CustomModelData in {key} ({path}).");
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
                                    throw new InvalidDataException(
                                        $"Unsupported Options.{optionName} in '{key}' ({path}).");
                            }
                        }
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Unsupported item property '{fieldName}' in '{key}' ({path}).");
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
                # Display/Lore are metadata only until item-component packets are supported.
                # diamond_pickaxe:
                #   Id: minecraft:diamond_pickaxe
                #   Display: "<aqua>Miner's Pickaxe</aqua>"
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
        var catalog = new ItemCatalog(Path.Combine(directory, "items-26.3.yml"), definitions);
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
        string lootDirectory = Path.Combine(ctx.DataDirectory, "loot");
        Directory.CreateDirectory(lootDirectory);
        string lootExample = Path.Combine(lootDirectory, "example-loot.yml");
        if (!File.Exists(lootExample))
            File.WriteAllText(lootExample, """
                # YAML entity loot tables; uncomment to configure.
                # cow:
                #   Drops:
                #     - { Item: minecraft:beef, Min: 1, Max: 3 }
                #     - { Item: minecraft:leather, Min: 0, Max: 2, Chance: 0.5 }
                #
                # guardian_cow:
                #   Drops:
                #     - { Item: minecraft:diamond, Min: 1, Max: 2 }
                #
                # To disable a bundled table, define Drops: [] or Enabled: false.
                """ + Environment.NewLine);
        var loot = new LootCatalog(catalog,
            Path.Combine(directory, "loot-26.3.yml"), lootDirectory);
        ctx.RegisterService<IShardItemRegistry>(catalog);
        ctx.RegisterService<IShardRecipeRegistry>(recipes);
        ctx.RegisterService<IShardLootRegistry>(loot);
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
                    $"max stack {item.MaxStackSize} | block item: {item.IsBlockItem}" +
                    (item.Display is null ? "" : $" | display: {item.Display}"));
                return true;
            }
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
        ctx.RegisterConsoleCommand("lootinfo", (args, token) =>
        {
            string key = args.Trim();
            bool exists = loot.TryRollEntity(key, out _, new Random(0));
            ShardLootTable? rule = loot.Definitions.FirstOrDefault(t =>
                string.Equals(t.EntityId, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.EntityId, key.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase)
                    ? key["minecraft:".Length..] : key, StringComparison.OrdinalIgnoreCase));
            if (!exists || rule is null)
                ctx.Log("Usage: lootinfo <entity-id> (known configured loot table)");
            else
                ctx.Log($"Loot {rule.EntityId}: " +
                    (rule.Drops.Count == 0 ? "(no drops)" :
                        string.Join(", ", rule.Drops.Select(d =>
                        $"{catalog.All[d.ItemId].Identifier} {d.Min}-{d.Max} @ {d.Chance:P0}"))));
            return Task.CompletedTask;
        });
        ctx.Log($"Registered {catalog.Count} vanilla Java {catalog.MinecraftVersion} " +
            $"item definitions, {recipes.RecipeCount} YAML recipes, " +
            $"and {loot.TableCount} entity loot tables; " +
            $"item overrides: {definitions}; recipe overrides: {recipeDirectory}; loot: {lootDirectory}.");
        return Task.CompletedTask;
    }

    public Task OnDisableAsync(CancellationToken ct) => Task.CompletedTask;
}
