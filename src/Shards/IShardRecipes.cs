namespace Amethyst.Shards;

/// <summary>Supported server-side workstation recipe types. These are not
/// client recipe-book packet IDs.</summary>
public enum ShardCookingStation { Furnace, Smoker, BlastFurnace }

/// <summary>A simple, component-free vanilla stack used at the recipe API
/// boundary. Item IDs come from IShardItemRegistry.</summary>
public readonly record struct ShardRecipeStack(int ItemId, int Count);

public readonly record struct ShardCraftingMatch(
    string RecipeId, ShardRecipeStack Result, int[] ConsumedSlots);

public readonly record struct ShardCookingMatch(
    string RecipeId, ShardRecipeStack Result, int CookingTicks);

/// <summary>Read-only, server-authoritative recipe lookup published by the
/// Items Shard. Caller inventories and workstations own ingredient removal,
/// item insertion, fuel consumption and persistence.</summary>
public interface IShardRecipeRegistry
{
    int RecipeCount { get; }
    IReadOnlyList<string> RecipeIds { get; }
    /// <summary>grid is row-major, exactly 9 slots, each with its current
    /// stack. Returns one ingredient slot index per item to consume.</summary>
    bool TryMatchCrafting(IReadOnlyList<ShardRecipeStack> grid,
        out ShardCraftingMatch match);
    bool TryGetCookingRecipe(ShardCookingStation station, int ingredientId,
        out ShardCookingMatch match);
}
