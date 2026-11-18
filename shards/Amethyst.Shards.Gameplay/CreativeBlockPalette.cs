namespace Amethyst;

/// <summary>Safe subset of plain, single-state vanilla block items for Creative placement.
/// Values are Java Edition 26.3 (protocol 777) item IDs and global block-state IDs.
/// Blocks with placement properties, block entities, physics or neighbor rules are
/// intentionally excluded until their gameplay handlers exist.</summary>
public static class CreativeBlockPalette
{
    private static readonly Dictionary<int, ushort> States = new()
    {
        [215] = 292, // minecraft:oak_leaves: distance=7,persistent=true,waterlogged=false
        [216] = 320, // minecraft:spruce_leaves: distance=7,persistent=true,waterlogged=false
        [217] = 348, // minecraft:birch_leaves: distance=7,persistent=true,waterlogged=false
        [218] = 376, // minecraft:jungle_leaves: distance=7,persistent=true,waterlogged=false
        [219] = 404, // minecraft:acacia_leaves: distance=7,persistent=true,waterlogged=false
        [221] = 460, // minecraft:dark_oak_leaves: distance=7,persistent=true,waterlogged=false
        [223] = 516, // minecraft:mangrove_leaves: distance=7,persistent=true,waterlogged=false
        [1] = 1, // minecraft:stone
        [2] = 2, // minecraft:granite
        [3] = 3, // minecraft:polished_granite
        [4] = 4, // minecraft:diorite
        [5] = 5, // minecraft:polished_diorite
        [6] = 6, // minecraft:andesite
        [7] = 7, // minecraft:polished_andesite
        [9] = 33776, // minecraft:cobbled_deepslate
        [10] = 34187, // minecraft:polished_deepslate
        [11] = 30517, // minecraft:calcite
        [12] = 26809, // minecraft:tuff
        [17] = 27220, // minecraft:polished_tuff
        [21] = 27632, // minecraft:tuff_bricks
        [53] = 33565, // minecraft:dripstone_block
        [55] = 10, // minecraft:dirt
        [56] = 11, // minecraft:coarse_dirt
        [58] = 33771, // minecraft:rooted_dirt
        [59] = 33772, // minecraft:mud
        [62] = 14, // minecraft:cobblestone
        [63] = 15, // minecraft:oak_planks
        [64] = 16, // minecraft:spruce_planks
        [65] = 17, // minecraft:birch_planks
        [66] = 18, // minecraft:jungle_planks
        [67] = 19, // minecraft:acacia_planks
        [68] = 20, // minecraft:cherry_planks
        [69] = 21, // minecraft:dark_oak_planks
        [70] = 25, // minecraft:pale_oak_planks
        [71] = 26, // minecraft:mangrove_planks
        [72] = 27, // minecraft:poplar_planks
        [73] = 28, // minecraft:bamboo_planks
        [74] = 24389, // minecraft:crimson_planks
        [75] = 24390, // minecraft:warped_planks
        [88] = 121, // minecraft:sand
        [91] = 126, // minecraft:red_sand
        [92] = 127, // minecraft:gravel
        [93] = 136, // minecraft:coal_ore
        [94] = 137, // minecraft:deepslate_coal_ore
        [95] = 134, // minecraft:iron_ore
        [96] = 135, // minecraft:deepslate_iron_ore
        [97] = 31147, // minecraft:copper_ore
        [98] = 31148, // minecraft:deepslate_copper_ore
        [99] = 132, // minecraft:gold_ore
        [100] = 133, // minecraft:deepslate_gold_ore
        [103] = 11315, // minecraft:emerald_ore
        [104] = 11316, // minecraft:deepslate_emerald_ore
        [105] = 662, // minecraft:lapis_ore
        [106] = 663, // minecraft:deepslate_lapis_ore
        [107] = 6863, // minecraft:diamond_ore
        [108] = 6864, // minecraft:deepslate_diamond_ore
        [109] = 138, // minecraft:nether_gold_ore
        [110] = 13079, // minecraft:nether_quartz_ore
        [112] = 14760, // minecraft:coal_block
        [113] = 35427, // minecraft:raw_iron_block
        [114] = 35428, // minecraft:raw_copper_block
        [115] = 35429, // minecraft:raw_gold_block
        [117] = 26759, // minecraft:amethyst_block
        [119] = 3831, // minecraft:iron_block
        [120] = 31139, // minecraft:copper_block
        [128] = 3830, // minecraft:gold_block
        [129] = 6865, // minecraft:diamond_block
        [130] = 25175, // minecraft:netherite_block
        [231] = 661, // minecraft:glass
        [232] = 30518, // minecraft:tinted_glass
        [233] = 664, // minecraft:lapis_block
        [234] = 677, // minecraft:sandstone
        [250] = 2409, // minecraft:white_wool
        [251] = 2410, // minecraft:orange_wool
        [252] = 2411, // minecraft:magenta_wool
        [253] = 2412, // minecraft:light_blue_wool
        [254] = 2413, // minecraft:yellow_wool
        [255] = 2414, // minecraft:lime_wool
        [256] = 2415, // minecraft:pink_wool
        [257] = 2416, // minecraft:gray_wool
        [258] = 2417, // minecraft:light_gray_wool
        [259] = 2418, // minecraft:cyan_wool
        [260] = 2419, // minecraft:purple_wool
        [261] = 2420, // minecraft:blue_wool
        [262] = 2421, // minecraft:brown_wool
        [263] = 2422, // minecraft:green_wool
        [264] = 2423, // minecraft:red_wool
        [265] = 2424, // minecraft:black_wool
        [333] = 33712, // minecraft:moss_block
        [372] = 15329, // minecraft:smooth_quartz
        [373] = 15330, // minecraft:smooth_red_sandstone
        [374] = 15328, // minecraft:smooth_sandstone
        [375] = 15321, // minecraft:smooth_stone
        [376] = 3832, // minecraft:bricks
        [393] = 4924, // minecraft:mossy_cobblestone
        [394] = 4925, // minecraft:obsidian
        [399] = 16687, // minecraft:purpur_block
        [412] = 8598, // minecraft:snow_block
        [415] = 8616, // minecraft:clay
        [433] = 8667, // minecraft:netherrack
        [434] = 8668, // minecraft:soul_sand
        [435] = 8669, // minecraft:soul_soil
        [438] = 35426, // minecraft:smooth_basalt
        [441] = 8686, // minecraft:glowstone
        [449] = 9488, // minecraft:stone_bricks
        [450] = 9489, // minecraft:mossy_stone_bricks
        [451] = 9490, // minecraft:cracked_stone_bricks
        [453] = 9492, // minecraft:packed_mud
        [454] = 9493, // minecraft:mud_bricks
        [455] = 35009, // minecraft:deepslate_bricks
        [457] = 34598, // minecraft:deepslate_tiles
        [509] = 11211, // minecraft:end_stone
        [514] = 11469, // minecraft:emerald_block
        [557] = 13090, // minecraft:quartz_block
        [561] = 13211, // minecraft:white_terracotta
        [562] = 13212, // minecraft:orange_terracotta
        [563] = 13213, // minecraft:magenta_terracotta
        [564] = 13214, // minecraft:light_blue_terracotta
        [565] = 13215, // minecraft:yellow_terracotta
        [566] = 13216, // minecraft:lime_terracotta
        [567] = 13217, // minecraft:pink_terracotta
        [568] = 13218, // minecraft:gray_terracotta
        [569] = 13219, // minecraft:light_gray_terracotta
        [570] = 13220, // minecraft:cyan_terracotta
        [571] = 13221, // minecraft:purple_terracotta
        [572] = 13222, // minecraft:blue_terracotta
        [573] = 13223, // minecraft:brown_terracotta
        [574] = 13224, // minecraft:green_terracotta
        [575] = 13225, // minecraft:red_terracotta
        [576] = 13226, // minecraft:black_terracotta
        [605] = 8768, // minecraft:white_stained_glass
        [606] = 8769, // minecraft:orange_stained_glass
        [607] = 8770, // minecraft:magenta_stained_glass
        [608] = 8771, // minecraft:light_blue_stained_glass
        [609] = 8772, // minecraft:yellow_stained_glass
        [610] = 8773, // minecraft:lime_stained_glass
        [611] = 8774, // minecraft:pink_stained_glass
        [612] = 8775, // minecraft:gray_stained_glass
        [613] = 8776, // minecraft:light_gray_stained_glass
        [614] = 8777, // minecraft:cyan_stained_glass
        [615] = 8778, // minecraft:purple_stained_glass
        [616] = 8779, // minecraft:blue_stained_glass
        [617] = 8780, // minecraft:brown_stained_glass
        [618] = 8781, // minecraft:green_stained_glass
        [619] = 8782, // minecraft:red_stained_glass
        [620] = 8783, // minecraft:black_stained_glass
        [637] = 14478, // minecraft:prismarine
        [638] = 14479, // minecraft:prismarine_bricks
        [639] = 14480, // minecraft:dark_prismarine
        [643] = 14739, // minecraft:sea_lantern
        [644] = 15094, // minecraft:red_sandstone
        [650] = 16826, // minecraft:magma_block
        [689] = 17011, // minecraft:white_concrete
        [690] = 17012, // minecraft:orange_concrete
        [691] = 17013, // minecraft:magenta_concrete
        [692] = 17014, // minecraft:light_blue_concrete
        [693] = 17015, // minecraft:yellow_concrete
        [694] = 17016, // minecraft:lime_concrete
        [695] = 17017, // minecraft:pink_concrete
        [696] = 17018, // minecraft:gray_concrete
        [697] = 17019, // minecraft:light_gray_concrete
        [698] = 17020, // minecraft:cyan_concrete
        [699] = 17021, // minecraft:purple_concrete
        [700] = 17022, // minecraft:blue_concrete
        [701] = 17023, // minecraft:brown_concrete
        [702] = 17024, // minecraft:green_concrete
        [703] = 17025, // minecraft:red_concrete
        [704] = 17026, // minecraft:black_concrete
        [831] = 14379, // minecraft:slime_block
        [832] = 25173, // minecraft:honey_block
        [1529] = 24333, // minecraft:shroomlight
        [1536] = 25177, // minecraft:crying_obsidian
        [1537] = 25188, // minecraft:blackstone
        [1541] = 25599, // minecraft:polished_blackstone
        [1545] = 25600, // minecraft:polished_blackstone_bricks
    };


    // Logs are placed with the axis selected by the clicked face.
    private static readonly Dictionary<int, (ushort X, ushort Y, ushort Z)> Logs = new()
    {
        [163] = (139, 140, 141), // minecraft:oak_log
        [164] = (142, 143, 144), // minecraft:spruce_log
        [165] = (145, 146, 147), // minecraft:birch_log
        [166] = (148, 149, 150), // minecraft:jungle_log
        [167] = (151, 152, 153), // minecraft:acacia_log
        [170] = (157, 158, 159), // minecraft:dark_oak_log
        [171] = (163, 164, 165), // minecraft:mangrove_log
    };

    public static bool TryGetState(int itemId, int face, out ushort state)
    {
        if (Logs.TryGetValue(itemId, out var axes))
        {
            state = face switch
            {
                2 or 3 => axes.Z,
                4 or 5 => axes.X,
                _ => axes.Y
            };
            return true;
        }
        return TryGetState(itemId, out state);
    }

    public static bool TryGetState(int itemId, out ushort state) =>
        States.TryGetValue(itemId, out state);
}
