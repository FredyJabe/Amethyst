namespace Amethyst;

public enum StationKind : byte
{
    Crafting = 0,
    Furnace = 1,
    Smoker = 2,
    BlastFurnace = 3
}

public readonly record struct CraftingMatch(ItemStack Output, int[] ConsumedSlots);

/// <summary>Fuel behavior remains a Gameplay responsibility. Recipe matching
/// and cooking outputs have moved to IShardRecipeRegistry in the Items Shard.</summary>
public static class StationRecipes
{
    public static int FuelTicks(int itemId) => itemId switch
    {
        1010 or 1011 => 1600, // coal or charcoal
        1060 => 100, // stick
        63 or 64 or 65 or 66 or 67 or 68 or 69 or 70 or 71 or 72 or 73 => 300,
        163 or 164 or 165 or 166 or 167 or 168 or 169 or 170 or 171 => 300,
        _ => 0
    };
}
