namespace Amethyst;

/// <summary>XP from ore blocks when broken with a harvestable tool in
/// Survival. Ore XP is spawned into the world, not granted immediately.
/// The complete loot-table XP system is a separate world-generation milestone.</summary>
public static class OreExperience
{
    private static readonly Dictionary<string, (int Min, int Max)> Rewards = new()
    {
        ["coal_ore"] = (0, 2),
        ["deepslate_coal_ore"] = (0, 2),
        ["diamond_ore"] = (3, 7),
        ["deepslate_diamond_ore"] = (3, 7),
        ["emerald_ore"] = (3, 7),
        ["deepslate_emerald_ore"] = (3, 7),
        ["lapis_ore"] = (2, 5),
        ["deepslate_lapis_ore"] = (2, 5),
        ["redstone_ore"] = (1, 5),
        ["deepslate_redstone_ore"] = (1, 5),
        ["nether_quartz_ore"] = (2, 5),
        ["nether_gold_ore"] = (0, 1)
    };

    public static int Roll(ushort state, Random? random = null)
    {
        random ??= Random.Shared;
        // The version-pinned MiningRules have first/last block-state IDs.
        string? name = null;
        // The complete map of 26.3 XP-generating ores is initialized below.
        foreach (var (first, last, id) in StateRanges)
            if (state >= first && state <= last) name = id;
        if (name is null || !Rewards.TryGetValue(name, out var range)) return 0;
        return random.Next(range.Min, range.Max + 1);
    }

    private static readonly (int First, int Last, string Name)[] StateRanges =
    [
        (136, 136, "coal_ore"),
        (137, 137, "deepslate_coal_ore"),
        (6863, 6863, "diamond_ore"),
        (6864, 6864, "deepslate_diamond_ore"),
        (11315, 11315, "emerald_ore"),
        (11316, 11316, "deepslate_emerald_ore"),
        (662, 662, "lapis_ore"),
        (663, 663, "deepslate_lapis_ore"),
        (8551, 8552, "redstone_ore"),
        (8553, 8554, "deepslate_redstone_ore"),
        (13079, 13079, "nether_quartz_ore"),
        (138, 138, "nether_gold_ore")
    ];
}
