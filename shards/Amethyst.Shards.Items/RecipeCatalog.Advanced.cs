using System.Text.Json;
using Amethyst.Shards;
using YamlDotNet.RepresentationModel;

namespace Amethyst.BuiltinShards;

/// <summary>Extended Minecraft Java 26.3 recipe serializers. These share the
/// vanilla item registry and the deterministic override/priority rules with
/// the original crafting and cooking catalog.</summary>
public sealed partial class RecipeCatalog
{
    private static bool TryParseKind(string raw, out RecipeKind kind)
    {
        string value = raw.Trim().ToLowerInvariant();
        if (value.StartsWith("minecraft:", StringComparison.Ordinal))
            value = value["minecraft:".Length..];
        kind = value switch
        {
            "shaped" or "crafting_shaped" => RecipeKind.Shaped,
            "shapeless" or "crafting_shapeless" => RecipeKind.Shapeless,
            "furnace" or "smelting" => RecipeKind.Furnace,
            "smoker" or "smoking" => RecipeKind.Smoker,
            "blastfurnace" or "blasting" => RecipeKind.BlastFurnace,
            "campfire" or "campfire_cooking" => RecipeKind.Campfire,
            "stonecutting" => RecipeKind.Stonecutting,
            "smithingtransform" or "smithing_transform" => RecipeKind.SmithingTransform,
            "smithingtrim" or "smithing_trim" => RecipeKind.SmithingTrim,
            "brewing" => RecipeKind.Brewing,
            "craftingtransmute" or "crafting_transmute" => RecipeKind.CraftingTransmute,
            "craftingdye" or "crafting_dye" => RecipeKind.CraftingDye,
            "craftingimbue" or "crafting_imbue" => RecipeKind.CraftingImbue,
            "craftingdecoratedpot" or "crafting_decorated_pot" => RecipeKind.CraftingDecoratedPot,
            "specialbannerduplicate" or "crafting_special_bannerduplicate" => RecipeKind.SpecialBannerDuplicate,
            "specialbookcloning" or "crafting_special_bookcloning" => RecipeKind.SpecialBookCloning,
            "specialfireworkrocket" or "crafting_special_firework_rocket" => RecipeKind.SpecialFireworkRocket,
            "specialfireworkstar" or "crafting_special_firework_star" => RecipeKind.SpecialFireworkStar,
            "specialfireworkstarfade" or "crafting_special_firework_star_fade" => RecipeKind.SpecialFireworkStarFade,
            "specialmapextending" or "crafting_special_mapextending" => RecipeKind.SpecialMapExtending,
            "specialrepairitem" or "crafting_special_repairitem" => RecipeKind.SpecialRepairItem,
            "specialshielddecoration" or "crafting_special_shielddecoration" => RecipeKind.SpecialShieldDecoration,
            _ => (RecipeKind)(-1)
        };
        return Enum.IsDefined(kind);
    }

    private Recipe ParseAdvancedRecipe(string id, YamlMappingNode node,
        string file, RecipeKind kind, Dictionary<string, YamlNode> props,
        int defaultPriority)
    {
        int priority = props.TryGetValue("Priority", out var pn)
            ? Integer(pn, file, id) : defaultPriority;
        if (priority is < -1000 or > 1000)
            throw Invalid(file, id, "Priority must be between -1000 and 1000.");

        string[] fields = kind switch
        {
            RecipeKind.Stonecutting =>
                ["Type", "Enabled", "Priority", "Ingredient", "Result", "Group"],
            RecipeKind.SmithingTransform or RecipeKind.SmithingTrim =>
                ["Type", "Enabled", "Priority", "Template", "Base", "Addition", "Result"],
            RecipeKind.Brewing =>
                ["Type", "Enabled", "Priority", "Input", "Reagent", "Output", "Result"],
            RecipeKind.CraftingTransmute =>
                ["Type", "Enabled", "Priority", "Input", "Material", "Result", "Category", "Group"],
            RecipeKind.CraftingDye or RecipeKind.CraftingImbue or RecipeKind.CraftingDecoratedPot =>
                ["Type", "Enabled", "Priority", "Ingredient", "Ingredients", "Input",
                 "Material", "Result", "Category", "Group"],
            _ => ["Type", "Enabled", "Priority", "Category", "Group"]
        };
        // Validate the type-specific shape, rather than silently accepting
        // misspelled or inapplicable keys from a recipe file.
        Fields(node, file, id, fields);

        HashSet<int>[] inputs = [];
        ShardRecipeStack output = default;
        bool executable = false;
        string? inputPotion = null, reagentPotion = null;

        if (kind == RecipeKind.Stonecutting)
        {
            inputs = [RequiredIngredient(props, "Ingredient", file, id)];
            output = RequiredOutput(props, "Result", file, id);
            executable = output.Components is null;
        }
        else if (kind is RecipeKind.SmithingTransform or RecipeKind.SmithingTrim)
        {
            inputs =
            [
                RequiredIngredient(props, "Template", file, id),
                RequiredIngredient(props, "Base", file, id),
                RequiredIngredient(props, "Addition", file, id)
            ];
            if (kind == RecipeKind.SmithingTransform)
            {
                output = RequiredOutput(props, "Result", file, id);
                // Caller must copy all base-stack components to the result.
                executable = true;
            }
            else if (props.ContainsKey("Result"))
                throw Invalid(file, id, "SmithingTrim has no fixed Result; it decorates the base item.");
        }
        else if (kind == RecipeKind.Brewing)
        {
            if (!props.TryGetValue("Input", out var input) ||
                !props.TryGetValue("Reagent", out var reagent))
                throw Invalid(file, id, "Brewing requires Input and Reagent.");
            (HashSet<int> inputIds, inputPotion) = ReadBrewingIngredient(input, file, id);
            (HashSet<int> reagentIds, reagentPotion) =
                ReadBrewingIngredient(reagent, file, id);
            inputs = [inputIds, reagentIds];
            string outputName = props.ContainsKey("Output") ? "Output" : "Result";
            if (props.ContainsKey("Output") && props.ContainsKey("Result"))
                throw Invalid(file, id, "Use Output or Result, not both.");
            output = RequiredOutput(props, outputName, file, id);
            executable = true;
        }
        else if (kind == RecipeKind.CraftingTransmute)
        {
            inputs =
            [
                RequiredIngredient(props, "Input", file, id),
                RequiredIngredient(props, "Material", file, id)
            ];
            output = RequiredOutput(props, "Result", file, id);
            // Transmutation must carry arbitrary input components into the
            // result. Component-free station stacks cannot yet do this.
        }
        else if (props.TryGetValue("Result", out var rawResult))
        {
            // Dynamic special serializers may declare an optional result
            // prototype. It is never treated as a fixed craftable output.
            output = ReadOutput(rawResult, file, id);
        }

        return new Recipe
        {
            Id = id, Kind = kind, Result = output, Priority = priority,
            CookingTicks = 0, Inputs = inputs, IsExecutable = executable,
            InputPotion = inputPotion, ReagentPotion = reagentPotion,
            ResultComponents = output.Components
        };
    }

    private HashSet<int> RequiredIngredient(
        IReadOnlyDictionary<string, YamlNode> props, string field,
        string file, string id)
    {
        if (!props.TryGetValue(field, out var node))
            throw Invalid(file, id, $"Missing {field}.");
        return IngredientSet(node, file, id);
    }

    private ShardRecipeStack RequiredOutput(
        IReadOnlyDictionary<string, YamlNode> props, string field,
        string file, string id)
    {
        if (!props.TryGetValue(field, out var node))
            throw Invalid(file, id, $"Missing {field}.");
        return ReadOutput(node, file, id);
    }

    private ShardRecipeStack ReadOutput(YamlNode node, string file, string id)
    {
        if (node is YamlScalarNode)
        {
            int item = ItemId(node, file, id);
            return new ShardRecipeStack(item, 1);
        }
        if (node is not YamlMappingNode map)
            throw Invalid(file, id, "Result must be an item ID or an item stack mapping.");
        var props = Fields(map, file, id + ".Result",
            "Id", "Amount", "Components");
        if (!props.TryGetValue("Id", out var itemNode))
            throw Invalid(file, id, "Result.Id is required.");
        int itemId = ItemId(itemNode, file, id);
        int count = props.TryGetValue("Amount", out var amount)
            ? Integer(amount, file, id) : 1;
        if (!_items.IsValidStack(itemId, count))
            throw Invalid(file, id, "Result exceeds item stack capacity.");
        IReadOnlyDictionary<string, string>? components =
            props.TryGetValue("Components", out var componentNode)
                ? ReadComponents(componentNode, file, id) : null;
        return new ShardRecipeStack(itemId, count) { Components = components };
    }

    private static IReadOnlyDictionary<string, string> ReadComponents(
        YamlNode node, string file, string id)
    {
        if (node is not YamlMappingNode map)
            throw Invalid(file, id, "Components must be a mapping.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map.Children)
        {
            string name = Scalar(key, file, id);
            if (!result.TryAdd(name, JsonSerializer.Serialize(YamlValue(value, file, id))))
                throw Invalid(file, id, $"Duplicate component '{name}'.");
        }
        return result;
    }

    private static object? YamlValue(YamlNode node, string file, string id)
    {
        if (node is YamlMappingNode map)
        {
            var value = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, child) in map.Children)
                if (!value.TryAdd(Scalar(key, file, id), YamlValue(child, file, id)))
                    throw Invalid(file, id, "Duplicate component property.");
            return value;
        }
        if (node is YamlSequenceNode sequence)
            return sequence.Children.Select(x => YamlValue(x, file, id)).ToArray();
        string scalar = Scalar(node, file, id);
        if (bool.TryParse(scalar, out bool boolean)) return boolean;
        if (long.TryParse(scalar, out long integer)) return integer;
        if (double.TryParse(scalar,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double number))
            return number;
        return scalar;
    }

    private (HashSet<int> Ids, string? Potion) ReadBrewingIngredient(
        YamlNode node, string file, string id)
    {
        if (node is not YamlMappingNode map)
            return (IngredientSet(node, file, id), null);
        var fields = Fields(map, file, id + ".Ingredient",
            "Item", "PotionContents");
        if (!fields.TryGetValue("Item", out var item))
            throw Invalid(file, id, "Brewing ingredient requires Item.");
        HashSet<int> ids = IngredientSet(item, file, id);
        if (!fields.TryGetValue("PotionContents", out var potionContents))
            return (ids, null);
        if (potionContents is not YamlMappingNode potionMap)
            throw Invalid(file, id, "PotionContents must be a mapping.");
        var pred = Fields(potionMap, file, id + ".PotionContents", "Potion");
        if (!pred.TryGetValue("Potion", out var potionNode))
            throw Invalid(file, id, "PotionContents.Potion is required.");
        return (ids, Scalar(potionNode, file, id));
    }

    private static bool PotionMatches(ShardRecipeStack stack, string? expected)
    {
        if (expected is null) return true;
        if (stack.Components is null ||
            !stack.Components.TryGetValue("minecraft:potion_contents", out var json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("potion", out var potion) &&
                potion.ValueKind == JsonValueKind.String &&
                string.Equals(potion.GetString(), expected,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    public IReadOnlyList<ShardSingleInputMatch> GetStonecuttingRecipes(int ingredientId)
    {
        if (ingredientId <= 0 || !_items.TryGetById(ingredientId, out _))
            return [];
        return _stonecutting.Where(recipe => recipe.Inputs[0].Contains(ingredientId))
            .Select(recipe => new ShardSingleInputMatch(recipe.Id, recipe.Result))
            .ToArray();
    }

    public bool TryMatchSmithing(ShardRecipeStack template,
        ShardRecipeStack baseItem, ShardRecipeStack addition,
        out ShardSmithingMatch match)
    {
        match = default;
        if (!_items.IsValidStack(template.ItemId, template.Count) ||
            !_items.IsValidStack(baseItem.ItemId, baseItem.Count) ||
            !_items.IsValidStack(addition.ItemId, addition.Count) ||
            template.Count == 0 || baseItem.Count == 0 || addition.Count == 0)
            return false;
        foreach (var recipe in _smithing)
        {
            if (recipe.Kind != RecipeKind.SmithingTransform ||
                !recipe.Inputs[0].Contains(template.ItemId) ||
                !recipe.Inputs[1].Contains(baseItem.ItemId) ||
                !recipe.Inputs[2].Contains(addition.ItemId)) continue;
            var components = new Dictionary<string, string>(StringComparer.Ordinal);
            if (baseItem.Components is not null)
                foreach (var (key, value) in baseItem.Components)
                    components[key] = value;
            if (recipe.Result.Components is not null)
                foreach (var (key, value) in recipe.Result.Components)
                    components[key] = value;
            ShardRecipeStack result = recipe.Result with
            {
                Components = components.Count == 0 ? null : components
            };
            match = new ShardSmithingMatch(recipe.Id, result, true);
            return true;
        }
        return false;
    }

    public bool TryMatchBrewing(ShardRecipeStack input,
        ShardRecipeStack reagent, out ShardBrewingMatch match)
    {
        match = default;
        if (!_items.IsValidStack(input.ItemId, input.Count) ||
            !_items.IsValidStack(reagent.ItemId, reagent.Count) ||
            input.Count == 0 || reagent.Count == 0) return false;
        foreach (var recipe in _brewing)
        {
            if (!recipe.Inputs[0].Contains(input.ItemId) ||
                !recipe.Inputs[1].Contains(reagent.ItemId) ||
                !PotionMatches(input, recipe.InputPotion) ||
                !PotionMatches(reagent, recipe.ReagentPotion)) continue;
            match = new ShardBrewingMatch(recipe.Id, recipe.Result);
            return true;
        }
        return false;
    }
}
