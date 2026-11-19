namespace Amethyst.Shards;

/// <summary>All 22 recipe serializers present in Minecraft Java 26.3.
/// These values describe recipe data; they are not client packet IDs.</summary>
public enum ShardRecipeType
{
    Shaped, Shapeless, Furnace, Smoker, BlastFurnace, Campfire,
    Stonecutting, SmithingTransform, SmithingTrim, Brewing,
    CraftingTransmute, CraftingDye, CraftingImbue, CraftingDecoratedPot,
    SpecialBannerDuplicate, SpecialBookCloning, SpecialFireworkRocket,
    SpecialFireworkStar, SpecialFireworkStarFade, SpecialMapExtending,
    SpecialRepairItem, SpecialShieldDecoration
}

public enum ShardCookingStation { Furnace, Smoker, BlastFurnace, Campfire }

/// <summary>A recipe stack in the Shard API. The basic Gameplay inventory
/// currently only retains ItemId and Count; components are exposed here for
/// future component-aware stations and dynamic recipes.</summary>
public readonly record struct ShardRecipeStack(int ItemId, int Count)
{
    public IReadOnlyDictionary<string, string>? Components { get; init; }
}

public readonly record struct ShardCraftingMatch(
    string RecipeId, ShardRecipeStack Result, int[] ConsumedSlots);

public readonly record struct ShardCookingMatch(
    string RecipeId, ShardRecipeStack Result, int CookingTicks);

public readonly record struct ShardSingleInputMatch(
    string RecipeId, ShardRecipeStack Result);

public readonly record struct ShardSmithingMatch(
    string RecipeId, ShardRecipeStack Result, bool CopiesBaseComponents);

public readonly record struct ShardBrewingMatch(
    string RecipeId, ShardRecipeStack Result);

/// <summary>A recipe's registered serializer type and execution capability.
/// Recipes that depend on per-stack components or specialized vanilla
/// algorithms are retained in the catalog, but cannot be executed by the
/// current component-free Gameplay workstation implementation.</summary>
public readonly record struct ShardRecipeDescriptor(
    string Id, ShardRecipeType Type, bool IsExecutable)
{
    /// <summary>Serializer-specific YAML properties as JSON values.
    /// Specialized Shards can inspect them without losing recipe metadata.</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

public interface IShardRecipeRegistry
{
    int RecipeCount { get; }
    IReadOnlyList<string> RecipeIds { get; }
    IReadOnlyList<ShardRecipeDescriptor> Definitions { get; }

    /// <summary>Grid is row-major and exactly nine slots. Results never
    /// consume the ingredient stack until the caller applies ConsumedSlots.</summary>
    bool TryMatchCrafting(IReadOnlyList<ShardRecipeStack> grid,
        out ShardCraftingMatch match);

    bool TryGetCookingRecipe(ShardCookingStation station, int ingredientId,
        out ShardCookingMatch match);

    /// <summary>Returns all available stonecutter outputs, because the
    /// player chooses which one to craft.</summary>
    IReadOnlyList<ShardSingleInputMatch> GetStonecuttingRecipes(int ingredientId);

    /// <summary>Matching is component-safe: smithing and brewing recipes
    /// requiring data components are never reduced to unsafe plain stacks.</summary>
    bool TryMatchSmithing(ShardRecipeStack template, ShardRecipeStack baseItem,
        ShardRecipeStack addition, out ShardSmithingMatch match);
    bool TryMatchBrewing(ShardRecipeStack input, ShardRecipeStack reagent,
        out ShardBrewingMatch match);
}
