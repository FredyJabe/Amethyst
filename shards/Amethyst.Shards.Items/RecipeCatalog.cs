using System.Collections.ObjectModel;
using System.Globalization;
using Amethyst.Shards;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Amethyst.BuiltinShards;

/// <summary>Immutable recipe registry built from the Items Shard's version-pinned
/// vanilla item catalog and administrator YAML overrides. Recipe IDs are not
/// protocol IDs and do not imply recipe-book synchronization to the client.</summary>
public sealed partial class RecipeCatalog : IShardRecipeRegistry
{
    private enum RecipeKind
    {
        Shaped, Shapeless, Furnace, Smoker, BlastFurnace, Campfire,
        Stonecutting, SmithingTransform, SmithingTrim, Brewing,
        CraftingTransmute, CraftingDye, CraftingImbue, CraftingDecoratedPot,
        SpecialBannerDuplicate, SpecialBookCloning, SpecialFireworkRocket,
        SpecialFireworkStar, SpecialFireworkStarFade, SpecialMapExtending,
        SpecialRepairItem, SpecialShieldDecoration
    }

    private sealed class Recipe
    {
        public required string Id { get; init; }
        public required RecipeKind Kind { get; init; }
        public required ShardRecipeStack Result { get; init; }
        public required int Priority { get; init; }
        public required int CookingTicks { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public bool IsExecutable { get; init; } = true;
        public IReadOnlyDictionary<string, string>? Parameters { get; init; }
        public string? InputPotion { get; init; }
        public string? ReagentPotion { get; init; }
        public IReadOnlyDictionary<string, string>? ResultComponents { get; init; }
        // Shaped: row-major cells, empty slots are empty sets.
        // Shapeless: one set of acceptable items per single-unit ingredient.
        // Cooking: exactly one set of acceptable input materials.
        public required HashSet<int>[] Inputs { get; init; }
    }

    private readonly IShardItemRegistry _items;
    private readonly Recipe[] _crafting;
    private readonly Recipe[] _cooking;
    private readonly Recipe[] _stonecutting;
    private readonly Recipe[] _smithing;
    private readonly Recipe[] _brewing;
    private readonly IReadOnlyList<ShardRecipeDescriptor> _definitions;
    public IReadOnlyList<ShardRecipeDescriptor> Definitions => _definitions;
    private readonly IReadOnlyList<string> _ids;
    public int RecipeCount => _ids.Count;
    public IReadOnlyList<string> RecipeIds => _ids;

    public RecipeCatalog(IShardItemRegistry items, string builtInFile,
        string? customDirectory = null)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        ArgumentException.ThrowIfNullOrWhiteSpace(builtInFile);
        var entries = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, node) in ReadFile(builtInFile))
        {
            var recipe = ParseRecipe(id, node, builtInFile, 0);
            if (recipe is null) throw new InvalidDataException(
                $"Bundled recipe '{id}' cannot be disabled.");
            entries.Add(id, recipe);
        }

        if (customDirectory is not null)
        {
            Directory.CreateDirectory(customDirectory);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(customDirectory, "*",
                SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
                foreach (var (id, node) in ReadFile(file))
                {
                    if (!visited.Add(id)) throw new InvalidDataException(
                        $"Recipe '{id}' defined multiple times in user recipe files.");
                    Recipe? recipe = ParseRecipe(id, node, file, 100);
                    if (recipe is null)
                    {
                        if (!entries.Remove(id)) throw InvalidDataExceptionForUnknown(id, file);
                    }
                    else entries[id] = recipe;
                }
        }

        Recipe[] sorted = entries.Values.OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        _crafting = sorted.Where(r => r.IsExecutable && r.Kind is RecipeKind.Shaped or RecipeKind.Shapeless).ToArray();
        _cooking = sorted.Where(r => r.IsExecutable &&
            r.Kind is (RecipeKind.Furnace or RecipeKind.Smoker or
                RecipeKind.BlastFurnace or RecipeKind.Campfire)).ToArray();
        _stonecutting = sorted.Where(r => r.IsExecutable && r.Kind == RecipeKind.Stonecutting).ToArray();
        _smithing = sorted.Where(r => r.IsExecutable &&
            r.Kind is (RecipeKind.SmithingTransform or RecipeKind.SmithingTrim)).ToArray();
        _brewing = sorted.Where(r => r.IsExecutable && r.Kind == RecipeKind.Brewing).ToArray();
        _definitions = new ReadOnlyCollection<ShardRecipeDescriptor>(
            sorted.Select(r => new ShardRecipeDescriptor(r.Id,
                (ShardRecipeType)r.Kind, r.IsExecutable)
                { Parameters = r.Parameters }).ToArray());
        _ids = new ReadOnlyCollection<string>(sorted.Select(x => x.Id).ToArray());
    }

    public bool TryMatchCrafting(IReadOnlyList<ShardRecipeStack> grid,
        out ShardCraftingMatch match)
    {
        match = default;
        if (grid.Count != 9 || grid.Any(s => !_items.IsValidStack(s.ItemId, s.Count)))
            return false;
        foreach (Recipe recipe in _crafting)
        {
            int[]? consumed = recipe.Kind == RecipeKind.Shaped
                ? MatchShaped(recipe, grid)
                : MatchShapeless(recipe, grid);
            if (consumed is null) continue;
            match = new ShardCraftingMatch(recipe.Id, recipe.Result, consumed);
            return true;
        }
        return false;
    }

    public bool TryGetCookingRecipe(ShardCookingStation station, int ingredientId,
        out ShardCookingMatch match)
    {
        match = default;
        if (ingredientId <= 0 || !_items.TryGetById(ingredientId, out _)) return false;
        RecipeKind wanted = station switch
        {
            ShardCookingStation.Furnace => RecipeKind.Furnace,
            ShardCookingStation.Smoker => RecipeKind.Smoker,
            ShardCookingStation.BlastFurnace => RecipeKind.BlastFurnace,
            ShardCookingStation.Campfire => RecipeKind.Campfire,
            _ => throw new ArgumentOutOfRangeException(nameof(station))
        };
        foreach (Recipe recipe in _cooking)
            if (recipe.Kind == wanted && recipe.Inputs[0].Contains(ingredientId))
            {
                match = new ShardCookingMatch(recipe.Id, recipe.Result, recipe.CookingTicks);
                return true;
            }
        return false;
    }

    private static int[]? MatchShaped(Recipe recipe, IReadOnlyList<ShardRecipeStack> grid)
    {
        for (int top = 0; top <= 3 - recipe.Height; top++)
        for (int left = 0; left <= 3 - recipe.Width; left++)
        for (int flip = 0; flip < 2; flip++)
        {
            var consumed = new List<int>();
            bool valid = true;
            for (int y = 0; y < 3 && valid; y++)
            for (int x = 0; x < 3; x++)
            {
                int rx = x - left, ry = y - top;
                bool inside = rx >= 0 && rx < recipe.Width &&
                              ry >= 0 && ry < recipe.Height;
                HashSet<int>? allowed = inside
                    ? recipe.Inputs[ry * recipe.Width + (flip == 0 ? rx : recipe.Width - rx - 1)]
                    : null;
                var stack = grid[y * 3 + x];
                if (allowed is null || allowed.Count == 0)
                {
                    if (stack.Count != 0) { valid = false; break; }
                }
                else if (stack.Count == 0 || !allowed.Contains(stack.ItemId))
                {
                    valid = false;
                    break;
                }
                else consumed.Add(y * 3 + x);
            }
            if (valid) return consumed.ToArray();
        }
        return null;
    }

    private static int[]? MatchShapeless(Recipe recipe, IReadOnlyList<ShardRecipeStack> grid)
    {
        int[] slots = Enumerable.Range(0, 9).Where(i => grid[i].Count > 0).ToArray();
        if (slots.Length != recipe.Inputs.Length) return null;
        var used = new bool[slots.Length];
        var matches = new int[recipe.Inputs.Length];
        bool Find(int ingredient)
        {
            if (ingredient == recipe.Inputs.Length) return true;
            for (int i = 0; i < slots.Length; i++)
            {
                if (used[i] || !recipe.Inputs[ingredient].Contains(grid[slots[i]].ItemId))
                    continue;
                used[i] = true;
                matches[ingredient] = slots[i];
                if (Find(ingredient + 1)) return true;
                used[i] = false;
            }
            return false;
        }
        return Find(0) ? matches : null;
    }

    private Recipe? ParseRecipe(string id, YamlMappingNode node, string file,
        int defaultPriority)
    {
        var props = Fields(node, file, id,
            "Type", "Pattern", "Key", "Ingredients", "Ingredient", "Result",
            "CookingTime", "Enabled", "Priority",
            "Input", "Output", "Template", "Base", "Addition", "Reagent",
            "Material", "Category", "Group", "Experience", "Components",
            "ShowNotification", "show_notification", "Target", "Dye", "Source",
            "Banner", "Back", "Left", "Right", "Front", "Shell", "Fuel", "Star",
            "Trail", "Twinkle", "Shapes", "Map", "AllowedGenerations",
            "allowed_generations", "MaterialCount", "material_count",
            "MinMaterialCount", "MaxMaterialCount", "KeepInput",
            "AddMaterialCountToResult", "add_material_count_to_result");
        if (props.TryGetValue("Enabled", out var enableNode) &&
            !Boolean(enableNode, file, id)) return null;

        if (!props.TryGetValue("Type", out var kindNode) ||
            !TryParseKind(Scalar(kindNode, file, id), out RecipeKind kind))
            throw Invalid(file, id, "Unknown Minecraft Java 26.3 recipe type.");

        if (kind is not (RecipeKind.Shaped or RecipeKind.Shapeless or
            RecipeKind.Furnace or RecipeKind.Smoker or RecipeKind.BlastFurnace or
            RecipeKind.Campfire))
            return ParseAdvancedRecipe(id, node, file, kind, props, defaultPriority);

        if (!props.TryGetValue("Result", out var resultNode))
            throw Invalid(file, id, "Missing Result.");
        ShardRecipeStack output = ReadOutput(resultNode, file, id);

        int priority = props.TryGetValue("Priority", out var priorityNode)
            ? Integer(priorityNode, file, id) : defaultPriority;
        if (priority is < -1000 or > 1000)
            throw Invalid(file, id, "Priority must be between -1000 and 1000.");
        int cooking = 0;
        HashSet<int>[] inputs;
        int width = 0, height = 0;
        if (kind == RecipeKind.Shaped)
        {
            if (props.ContainsKey("Ingredients") || props.ContainsKey("Ingredient") ||
                props.ContainsKey("CookingTime") ||
                !props.TryGetValue("Pattern", out var patternNode) ||
                patternNode is not YamlSequenceNode patterns ||
                !props.TryGetValue("Key", out var keysNode) ||
                keysNode is not YamlMappingNode keys)
                throw Invalid(file, id, "Shaped recipes require Pattern and Key only.");
            if (patterns.Children.Count is < 1 or > 3)
                throw Invalid(file, id, "Pattern must have 1–3 rows.");
            var rows = patterns.Children.Select(row => Scalar(row, file, id)).ToArray();
            width = rows[0].Length;
            height = rows.Length;
            if (width is < 1 or > 3 || rows.Any(row => row.Length != width))
                throw Invalid(file, id, "Pattern rows must all have a width of 1–3.");
            var symbols = new Dictionary<char, HashSet<int>>();
            foreach (var (symbolNode, ingredientNode) in keys.Children)
            {
                string symbol = Scalar(symbolNode, file, id);
                if (symbol.Length != 1 || symbol[0] == ' ' || symbols.ContainsKey(symbol[0]))
                    throw Invalid(file, id, "Key symbols must be unique nonspace characters.");
                symbols.Add(symbol[0], IngredientSet(ingredientNode, file, id));
            }
            if (symbols.Count == 0 || rows.All(row => row.All(c => c == ' ')) ||
                symbols.Keys.Any(symbol => !rows.Any(row => row.Contains(symbol))))
                throw Invalid(file, id, "Pattern must use every Key symbol and contain an ingredient.");
            var cells = new List<HashSet<int>>();
            foreach (string row in rows)
                foreach (char symbol in row)
                {
                    if (symbol == ' ') { cells.Add([]); continue; }
                    if (!symbols.TryGetValue(symbol, out var ingredient))
                        throw Invalid(file, id, $"Missing Key mapping for '{symbol}'.");
                    cells.Add(ingredient);
                }
            inputs = cells.ToArray();
        }
        else if (kind == RecipeKind.Shapeless)
        {
            if (props.ContainsKey("Pattern") || props.ContainsKey("Key") ||
                props.ContainsKey("Ingredient") || props.ContainsKey("CookingTime") ||
                !props.TryGetValue("Ingredients", out var listNode) ||
                listNode is not YamlSequenceNode list ||
                list.Children.Count is < 1 or > 9)
                throw Invalid(file, id, "Shapeless recipes require 1–9 Ingredients.");
            inputs = list.Children.Select(v => IngredientSet(v, file, id)).ToArray();
        }
        else
        {
            if (props.ContainsKey("Ingredients") || props.ContainsKey("Pattern") ||
                props.ContainsKey("Key") ||
                !props.TryGetValue("Ingredient", out var inputNode))
                throw Invalid(file, id, "Cooking recipes require Ingredient, not Ingredients or Pattern.");
            inputs = [IngredientSet(inputNode, file, id)];
            cooking = props.TryGetValue("CookingTime", out var timeNode)
                ? Integer(timeNode, file, id)
                : kind == RecipeKind.Campfire ? 600 :
                  kind == RecipeKind.Furnace ? 200 : 100;
            if (cooking is < 1 or > 32767)
                throw Invalid(file, id, "CookingTime must be 1–32767 ticks.");
        }
        return new Recipe
        {
            Id = id, Kind = kind, Result = output,
            Priority = priority, CookingTicks = cooking, Inputs = inputs,
            Width = width, Height = height, IsExecutable = output.Components is null
        };
    }

    private HashSet<int> IngredientSet(YamlNode node, string file, string recipeId)
    {
        var result = new HashSet<int>();
        if (node is YamlSequenceNode sequence)
        {
            if (sequence.Children.Count is < 1 or > 1658)
                throw Invalid(file, recipeId, "Ingredient alternatives list must not be empty.");
            foreach (YamlNode option in sequence.Children)
                result.Add(ItemId(option, file, recipeId));
        }
        else result.Add(ItemId(node, file, recipeId));
        return result;
    }

    private int ItemId(YamlNode node, string file, string recipeId)
    {
        string value = Scalar(node, file, recipeId).Trim();
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
            out int numeric))
        {
            if (_items.TryGetById(numeric, out var byNumber) && byNumber.ProtocolId > 0)
                return byNumber.ProtocolId;
        }
        else if (_items.TryGetByIdentifier(value, out var byName) && byName.ProtocolId > 0)
            return byName.ProtocolId;
        throw Invalid(file, recipeId, $"Unknown ingredient or result item '{value}'.");
    }

    private static IEnumerable<(string Id, YamlMappingNode Node)> ReadFile(string file)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = File.OpenText(file);
            stream.Load(reader);
        }
        catch (YamlException ex) { throw new InvalidDataException(
            $"Invalid recipes YAML in '{file}': {ex.Message}", ex); }
        if (stream.Documents.Count == 0) yield break;
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException($"Recipes in '{file}' require a root YAML mapping.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyNode, valueNode) in root.Children)
        {
            string id = Scalar(keyNode, file, "root").Trim().ToLowerInvariant();
            if (id.Length is < 1 or > 128 ||
                !id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ||
                    c is '_' or '-' or ':' or '.' or '/'))
                throw Invalid(file, id, "Invalid recipe ID.");
            if (!seen.Add(id)) throw Invalid(file, id, "Duplicate recipe ID.");
            if (valueNode is not YamlMappingNode node)
                throw Invalid(file, id, "Each recipe must be a mapping.");
            yield return (id, node);
        }
    }

    private static Dictionary<string, YamlNode> Fields(YamlMappingNode node,
        string file, string recipeId, params string[] allowed)
    {
        var names = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        var output = new Dictionary<string, YamlNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in node.Children)
        {
            string field = Scalar(key, file, recipeId);
            if (!names.Contains(field)) throw Invalid(file, recipeId,
                $"Unsupported property '{field}'.");
            if (!output.TryAdd(field, value))
                throw Invalid(file, recipeId, $"Duplicate property '{field}'.");
        }
        return output;
    }

    private static string Scalar(YamlNode node, string file, string recipeId) =>
        node is YamlScalarNode { Value: { } value } ? value :
            throw Invalid(file, recipeId, "Expected a scalar value.");

    private static int Integer(YamlNode node, string file, string recipeId) =>
        int.TryParse(Scalar(node, file, recipeId), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int n) ? n :
            throw Invalid(file, recipeId, "Expected an integer.");

    private static bool Boolean(YamlNode node, string file, string recipeId) =>
        bool.TryParse(Scalar(node, file, recipeId), out bool b) ? b :
            throw Invalid(file, recipeId, "Expected true or false.");

    private static InvalidDataException InvalidDataExceptionForUnknown(
        string id, string file) => Invalid(file, id,
            "Enabled: false requires an existing bundled recipe ID.");

    private static InvalidDataException Invalid(string file, string id, string reason) =>
        new($"Invalid recipe '{id}' in '{file}': {reason}");
}
