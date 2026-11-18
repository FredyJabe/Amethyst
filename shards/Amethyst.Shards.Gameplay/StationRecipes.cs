namespace Amethyst;

public enum StationKind : byte
{
    Crafting = 0,
    Furnace = 1,
    Smoker = 2,
    BlastFurnace = 3
}

public readonly record struct CraftingMatch(ItemStack Output, int[] ConsumedSlots);

/// <summary>Conservative, version-pinned Java 26.3 recipe subset.
/// Match by authoritative input stacks, never by a client recipe ID or its
/// changed-slot claims. The small registry is intentionally explicit until
/// the full vanilla recipe/tag data is imported.</summary>
public static class StationRecipes
{
    // Tagged ingredient IDs used by the recipe matcher; they aren't item IDs.
    private const int AnyPlank = -1;
    private const int AnyLog = -2;
    private const int AnyStoneCraft = -3;
    private static readonly HashSet<int> Planks = [63,64,65,66,67,68,69,70,71,72,73,74,75];
    private static readonly HashSet<int> Logs = [163,164,165,166,167,168,169,170,171,172,173,174,175];
    private static readonly HashSet<int> StoneCraft = [62,9,1537]; // cobblestone, cobbled deepslate, blackstone
    private readonly record struct Recipe(int OutputId, int Count, int Width,
        int Height, int[] Ingredients);
    private static Recipe R(int id, int count, int width, params int[] ingredients) =>
        new(id, count, width, ingredients.Length / width, ingredients);

    private static readonly Recipe[] Crafting =
    [
        R(1060, 4, 1, AnyPlank, AnyPlank), // sticks
        R(405, 1, 2, AnyPlank, AnyPlank, AnyPlank, AnyPlank), // crafting table
        R(404, 1, 3, AnyPlank, AnyPlank, AnyPlank,
            AnyPlank, 0, AnyPlank, AnyPlank, AnyPlank, AnyPlank), // chest
        R(407, 1, 3, AnyStoneCraft, AnyStoneCraft, AnyStoneCraft,
            AnyStoneCraft, 0, AnyStoneCraft,
            AnyStoneCraft, AnyStoneCraft, AnyStoneCraft), // furnace
        R(395, 4, 1, 1010, 1060), // torch from coal
        R(395, 4, 1, 1011, 1060), // torch from charcoal
        R(1507, 1, 3, 0, AnyLog, 0,
            AnyLog, 407, AnyLog, 0, AnyLog, 0), // smoker
        R(1508, 1, 3, 1018,1018,1018,
            1018,407,1018,375,375,375), // blast furnace
        R(1027, 1, 3, AnyPlank,AnyPlank,AnyPlank,
            0,1060,0, 0,1060,0), // wooden pickaxe
        R(1037, 1, 3, AnyStoneCraft,AnyStoneCraft,AnyStoneCraft,
            0,1060,0, 0,1060,0), // stone pickaxe
        R(1047, 1, 3, 1018,1018,1018,
            0,1060,0, 0,1060,0), // iron pickaxe
        R(1026, 1, 1, AnyPlank,1060,1060), // wooden shovel
        R(1036, 1, 1, AnyStoneCraft,1060,1060), // stone shovel
        R(1046, 1, 1, 1018,1060,1060), // iron shovel
        R(1028, 1, 2, AnyPlank,AnyPlank,
            AnyPlank,1060,0,1060), // wooden axe
        R(1038, 1, 2, AnyStoneCraft,AnyStoneCraft,
            AnyStoneCraft,1060,0,1060), // stone axe
        R(1048, 1, 2, 1018,1018,1018,1060,0,1060) // iron axe
    ];

    private static bool Matches(int itemId, int expected) => expected switch
    {
        0 => itemId == 0,
        AnyPlank => Planks.Contains(itemId),
        AnyLog => Logs.Contains(itemId),
        AnyStoneCraft => StoneCraft.Contains(itemId),
        _ => itemId == expected
    };

    public static CraftingMatch? MatchCrafting(ItemStack[] grid)
    {
        if (grid.Length != 9) return null;
        // Vanilla log-to-planks is a shapeless one-ingredient recipe. Preserve
        // the wood species with a version-pinned item ID mapping.
        var filled = Enumerable.Range(0, 9).Where(i => grid[i].Count > 0).ToArray();
        if (filled.Length == 1)
        {
            int log = grid[filled[0]].ItemId;
            int planks = log switch
            {
                163 => 63, 164 => 64, 165 => 65, 166 => 66, 167 => 67,
                168 => 68, 169 => 70, 170 => 69, 171 => 71,
                _ => 0
            };
            if (planks != 0) return new CraftingMatch(new ItemStack(planks, 4), filled);
        }
        foreach (Recipe recipe in Crafting)
        {
            if (recipe.Ingredients.Length != recipe.Width * recipe.Height) continue;
            for (int y = 0; y <= 3 - recipe.Height; y++)
            for (int x = 0; x <= 3 - recipe.Width; x++)
            {
                var consumed = new List<int>();
                bool match = true;
                for (int gy = 0; gy < 3 && match; gy++)
                for (int gx = 0; gx < 3; gx++)
                {
                    int localX = gx - x, localY = gy - y;
                    int expected = localX >= 0 && localX < recipe.Width &&
                        localY >= 0 && localY < recipe.Height
                        ? recipe.Ingredients[localY * recipe.Width + localX] : 0;
                    int index = gy * 3 + gx;
                    int found = grid[index].Count > 0 ? grid[index].ItemId : 0;
                    if (!Matches(found, expected)) { match = false; break; }
                    if (expected != 0) consumed.Add(index);
                }
                if (match) return new CraftingMatch(
                    new ItemStack(recipe.OutputId, recipe.Count), consumed.ToArray());
            }
        }
        return null;
    }

    /// <summary>Version-pinned subset of vanilla 26.3 cooking recipes.
    /// Smoker accepts food; blast furnace accepts ores and raw metals.</summary>
    public static bool TryCook(StationKind kind, int input, out ItemStack result,
        out int durationTicks)
    {
        result = ItemStack.Empty;
        durationTicks = kind is StationKind.Smoker or StationKind.BlastFurnace ? 100 : 200;
        int output = 0;
        bool food = input switch
        {
            1260 => (output = 1261) > 0, // beef -> steak
            1097 => (output = 1098) > 0, // porkchop
            1262 => (output = 1263) > 0, // chicken
            1415 => (output = 1416) > 0, // mutton
            1190 => (output = 1194) > 0, // cod
            1191 => (output = 1195) > 0, // salmon
            1379 => (output = 1380) > 0, // potato
            328 => (output = 1257) > 0, // kelp
            _ => false
        };
        bool ore = false;
        if (!food)
        {
            output = input switch
            {
                1017 or 95 or 96 => 1018, // raw iron and iron ore
                1019 or 97 or 98 => 1020, // raw copper
                1021 or 99 or 100 => 1022, // raw gold
                _ => 0
            };
            ore = output != 0;
        }
        if (!food && !ore && kind == StationKind.Furnace)
            output = input switch
            {
                62 => 1, // cobblestone -> stone
                1 => 375, // stone -> smooth stone
                88 or 91 => 231, // sand -> glass
                163 => 1011, // logs -> charcoal
                _ => 0
            };
        if (kind == StationKind.Smoker && !food ||
            kind == StationKind.BlastFurnace && !ore || output == 0)
            return false;
        result = new ItemStack(output, 1);
        return true;
    }

    public static int FuelTicks(int itemId) => itemId switch
    {
        1010 or 1011 => 1600, // coal or charcoal
        1060 => 100, // stick
        63 or 64 or 65 or 66 or 67 or 68 or 69 or 70 or 71 or 72 or 73 => 300,
        163 or 164 or 165 or 166 or 167 or 168 or 169 or 170 or 171 => 300,
        _ => 0
    };
}
