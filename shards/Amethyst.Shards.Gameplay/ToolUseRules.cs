namespace Amethyst;

/// <summary>Version-pinned Java 26.3 right-click tool transformations.
/// Generated from RosegoldMC/minecraft-data.cr 26.3 blocks.json.
/// Every variant is mapped by its block-state range and matching properties.</summary>
public static class ToolUseRules
{
    public readonly record struct ToolUse(ushort NewState, bool Adjacent,
        int Seeds = 0);

    private readonly record struct StateRange(int First, int Last, int Destination)
    {
        public bool TryMap(int source, out ushort state)
        {
            if (source < First || source > Last)
            {
                state = 0;
                return false;
            }
            state = (ushort)(Destination + source - First);
            return true;
        }
    }

    // Each pair below preserves every variant's axis, facing, half,
    // waterlogged and other shared block-state properties.
    private static readonly StateRange[] Strip =
    [
        new(22, 24, 258), // pale_oak_wood -> stripped_pale_oak_wood
        new(139, 141, 198), // oak_log -> stripped_oak_log
        new(142, 144, 177), // spruce_log -> stripped_spruce_log
        new(145, 147, 180), // birch_log -> stripped_birch_log
        new(148, 150, 183), // jungle_log -> stripped_jungle_log
        new(151, 153, 186), // acacia_log -> stripped_acacia_log
        new(154, 156, 189), // cherry_log -> stripped_cherry_log
        new(157, 159, 192), // dark_oak_log -> stripped_dark_oak_log
        new(160, 162, 195), // pale_oak_log -> stripped_pale_oak_log
        new(163, 165, 201), // mangrove_log -> stripped_mangrove_log
        new(166, 168, 204), // poplar_log -> stripped_poplar_log
        new(174, 176, 207), // bamboo_block -> stripped_bamboo_block
        new(210, 212, 237), // oak_wood -> stripped_oak_wood
        new(213, 215, 240), // spruce_wood -> stripped_spruce_wood
        new(216, 218, 243), // birch_wood -> stripped_birch_wood
        new(219, 221, 246), // jungle_wood -> stripped_jungle_wood
        new(222, 224, 249), // acacia_wood -> stripped_acacia_wood
        new(225, 227, 252), // cherry_wood -> stripped_cherry_wood
        new(228, 230, 255), // dark_oak_wood -> stripped_dark_oak_wood
        new(231, 233, 261), // mangrove_wood -> stripped_mangrove_wood
        new(234, 236, 264), // poplar_wood -> stripped_poplar_wood
        new(24302, 24304, 24305), // warped_stem -> stripped_warped_stem
        new(24308, 24310, 24311), // warped_hyphae -> stripped_warped_hyphae
        new(24319, 24321, 24322), // crimson_stem -> stripped_crimson_stem
        new(24325, 24327, 24328), // crimson_hyphae -> stripped_crimson_hyphae
    ];
    private static readonly StateRange[] Unwax =
    [
        new(9852, 9883, 9724), // waxed_copper_bars -> copper_bars
        new(9884, 9915, 9756), // waxed_exposed_copper_bars -> exposed_copper_bars
        new(9916, 9947, 9788), // waxed_weathered_copper_bars -> weathered_copper_bars
        new(9948, 9979, 9820), // waxed_oxidized_copper_bars -> oxidized_copper_bars
        new(10010, 10015, 9986), // waxed_copper_chain -> copper_chain
        new(10016, 10021, 9992), // waxed_exposed_copper_chain -> exposed_copper_chain
        new(10022, 10027, 9998), // waxed_weathered_copper_chain -> weathered_copper_chain
        new(10028, 10033, 10004), // waxed_oxidized_copper_chain -> oxidized_copper_chain
        new(24218, 24221, 24202), // waxed_copper_lantern -> copper_lantern
        new(24222, 24225, 24206), // waxed_exposed_copper_lantern -> exposed_copper_lantern
        new(24226, 24229, 24210), // waxed_weathered_copper_lantern -> weathered_copper_lantern
        new(24230, 24233, 24214), // waxed_oxidized_copper_lantern -> oxidized_copper_lantern
        new(31143, 31143, 31139), // waxed_copper_block -> copper_block
        new(31144, 31144, 31140), // waxed_exposed_copper -> exposed_copper
        new(31145, 31145, 31141), // waxed_weathered_copper -> weathered_copper
        new(31146, 31146, 31142), // waxed_oxidized_copper -> oxidized_copper
        new(31153, 31153, 31149), // waxed_cut_copper -> cut_copper
        new(31154, 31154, 31150), // waxed_exposed_cut_copper -> exposed_cut_copper
        new(31155, 31155, 31151), // waxed_weathered_cut_copper -> weathered_cut_copper
        new(31156, 31156, 31152), // waxed_oxidized_cut_copper -> oxidized_cut_copper
        new(31161, 31161, 31157), // waxed_chiseled_copper -> chiseled_copper
        new(31162, 31162, 31158), // waxed_exposed_chiseled_copper -> exposed_chiseled_copper
        new(31163, 31163, 31159), // waxed_weathered_chiseled_copper -> weathered_chiseled_copper
        new(31164, 31164, 31160), // waxed_oxidized_chiseled_copper -> oxidized_chiseled_copper
        new(31485, 31564, 31165), // waxed_cut_copper_stairs -> cut_copper_stairs
        new(31565, 31644, 31245), // waxed_exposed_cut_copper_stairs -> exposed_cut_copper_stairs
        new(31645, 31724, 31325), // waxed_weathered_cut_copper_stairs -> weathered_cut_copper_stairs
        new(31725, 31804, 31405), // waxed_oxidized_cut_copper_stairs -> oxidized_cut_copper_stairs
        new(31829, 31834, 31805), // waxed_cut_copper_slab -> cut_copper_slab
        new(31835, 31840, 31811), // waxed_exposed_cut_copper_slab -> exposed_cut_copper_slab
        new(31841, 31846, 31817), // waxed_weathered_cut_copper_slab -> weathered_cut_copper_slab
        new(31847, 31852, 31823), // waxed_oxidized_cut_copper_slab -> oxidized_cut_copper_slab
        new(32109, 32172, 31853), // waxed_copper_door -> copper_door
        new(32173, 32236, 31917), // waxed_exposed_copper_door -> exposed_copper_door
        new(32237, 32300, 31981), // waxed_weathered_copper_door -> weathered_copper_door
        new(32301, 32364, 32045), // waxed_oxidized_copper_door -> oxidized_copper_door
        new(32621, 32684, 32365), // waxed_copper_trapdoor -> copper_trapdoor
        new(32685, 32748, 32429), // waxed_exposed_copper_trapdoor -> exposed_copper_trapdoor
        new(32749, 32812, 32493), // waxed_weathered_copper_trapdoor -> weathered_copper_trapdoor
        new(32813, 32876, 32557), // waxed_oxidized_copper_trapdoor -> oxidized_copper_trapdoor
        new(32885, 32886, 32877), // waxed_copper_grate -> copper_grate
        new(32887, 32888, 32879), // waxed_exposed_copper_grate -> exposed_copper_grate
        new(32889, 32890, 32881), // waxed_weathered_copper_grate -> weathered_copper_grate
        new(32891, 32892, 32883), // waxed_oxidized_copper_grate -> oxidized_copper_grate
        new(32909, 32912, 32893), // waxed_copper_bulb -> copper_bulb
        new(32913, 32916, 32897), // waxed_exposed_copper_bulb -> exposed_copper_bulb
        new(32917, 32920, 32901), // waxed_weathered_copper_bulb -> weathered_copper_bulb
        new(32921, 32924, 32905), // waxed_oxidized_copper_bulb -> oxidized_copper_bulb
        new(33021, 33044, 32925), // waxed_copper_chest -> copper_chest
        new(33045, 33068, 32949), // waxed_exposed_copper_chest -> exposed_copper_chest
        new(33069, 33092, 32973), // waxed_weathered_copper_chest -> weathered_copper_chest
        new(33093, 33116, 32997), // waxed_oxidized_copper_chest -> oxidized_copper_chest
        new(33245, 33276, 33117), // waxed_copper_golem_statue -> copper_golem_statue
        new(33277, 33308, 33149), // waxed_exposed_copper_golem_statue -> exposed_copper_golem_statue
        new(33309, 33340, 33181), // waxed_weathered_copper_golem_statue -> weathered_copper_golem_statue
        new(33341, 33372, 33213), // waxed_oxidized_copper_golem_statue -> oxidized_copper_golem_statue
        new(33469, 33492, 33373), // waxed_lightning_rod -> lightning_rod
        new(33493, 33516, 33397), // waxed_exposed_lightning_rod -> exposed_lightning_rod
        new(33517, 33540, 33421), // waxed_weathered_lightning_rod -> weathered_lightning_rod
        new(33541, 33564, 33445), // waxed_oxidized_lightning_rod -> oxidized_lightning_rod
    ];
    private static readonly StateRange[] Scrape =
    [
        new(9756, 9787, 9724), // exposed_copper_bars -> copper_bars
        new(9788, 9819, 9756), // weathered_copper_bars -> exposed_copper_bars
        new(9820, 9851, 9788), // oxidized_copper_bars -> weathered_copper_bars
        new(9992, 9997, 9986), // exposed_copper_chain -> copper_chain
        new(9998, 10003, 9992), // weathered_copper_chain -> exposed_copper_chain
        new(10004, 10009, 9998), // oxidized_copper_chain -> weathered_copper_chain
        new(24206, 24209, 24202), // exposed_copper_lantern -> copper_lantern
        new(24210, 24213, 24206), // weathered_copper_lantern -> exposed_copper_lantern
        new(24214, 24217, 24210), // oxidized_copper_lantern -> weathered_copper_lantern
        new(31140, 31140, 31139), // exposed_copper -> copper_block
        new(31141, 31141, 31140), // weathered_copper -> exposed_copper
        new(31142, 31142, 31141), // oxidized_copper -> weathered_copper
        new(31150, 31150, 31149), // exposed_cut_copper -> cut_copper
        new(31151, 31151, 31150), // weathered_cut_copper -> exposed_cut_copper
        new(31152, 31152, 31151), // oxidized_cut_copper -> weathered_cut_copper
        new(31158, 31158, 31157), // exposed_chiseled_copper -> chiseled_copper
        new(31159, 31159, 31158), // weathered_chiseled_copper -> exposed_chiseled_copper
        new(31160, 31160, 31159), // oxidized_chiseled_copper -> weathered_chiseled_copper
        new(31245, 31324, 31165), // exposed_cut_copper_stairs -> cut_copper_stairs
        new(31325, 31404, 31245), // weathered_cut_copper_stairs -> exposed_cut_copper_stairs
        new(31405, 31484, 31325), // oxidized_cut_copper_stairs -> weathered_cut_copper_stairs
        new(31811, 31816, 31805), // exposed_cut_copper_slab -> cut_copper_slab
        new(31817, 31822, 31811), // weathered_cut_copper_slab -> exposed_cut_copper_slab
        new(31823, 31828, 31817), // oxidized_cut_copper_slab -> weathered_cut_copper_slab
        new(31917, 31980, 31853), // exposed_copper_door -> copper_door
        new(31981, 32044, 31917), // weathered_copper_door -> exposed_copper_door
        new(32045, 32108, 31981), // oxidized_copper_door -> weathered_copper_door
        new(32429, 32492, 32365), // exposed_copper_trapdoor -> copper_trapdoor
        new(32493, 32556, 32429), // weathered_copper_trapdoor -> exposed_copper_trapdoor
        new(32557, 32620, 32493), // oxidized_copper_trapdoor -> weathered_copper_trapdoor
        new(32879, 32880, 32877), // exposed_copper_grate -> copper_grate
        new(32881, 32882, 32879), // weathered_copper_grate -> exposed_copper_grate
        new(32883, 32884, 32881), // oxidized_copper_grate -> weathered_copper_grate
        new(32897, 32900, 32893), // exposed_copper_bulb -> copper_bulb
        new(32901, 32904, 32897), // weathered_copper_bulb -> exposed_copper_bulb
        new(32905, 32908, 32901), // oxidized_copper_bulb -> weathered_copper_bulb
        new(32949, 32972, 32925), // exposed_copper_chest -> copper_chest
        new(32973, 32996, 32949), // weathered_copper_chest -> exposed_copper_chest
        new(32997, 33020, 32973), // oxidized_copper_chest -> weathered_copper_chest
        new(33149, 33180, 33117), // exposed_copper_golem_statue -> copper_golem_statue
        new(33181, 33212, 33149), // weathered_copper_golem_statue -> exposed_copper_golem_statue
        new(33213, 33244, 33181), // oxidized_copper_golem_statue -> weathered_copper_golem_statue
        new(33397, 33420, 33373), // exposed_lightning_rod -> lightning_rod
        new(33421, 33444, 33397), // weathered_lightning_rod -> exposed_lightning_rod
        new(33445, 33468, 33421), // oxidized_lightning_rod -> weathered_lightning_rod
    ];
    private static readonly StateRange[] Till =
    [
        new(8, 9, 6875), // grass_block
        new(10, 10, 6875), // dirt
        new(11, 11, 10), // coarse_dirt
        new(16796, 16796, 6875), // dirt_path
        new(33771, 33771, 10), // rooted_dirt
    ];
    private static readonly StateRange[] Path =
    [
        new(8, 9, 16796), // grass_block
        new(10, 10, 16796), // dirt
        new(11, 11, 16796), // coarse_dirt
        new(12, 13, 16796), // podzol
        new(10652, 10653, 16796), // mycelium
        new(33771, 33771, 16796), // rooted_dirt
    ];
    private static readonly Dictionary<ushort, ushort> Extinguish = new()
    {
        [(ushort)24234] = (ushort)24238,
        [(ushort)24235] = (ushort)24239,
        [(ushort)24236] = (ushort)24240,
        [(ushort)24237] = (ushort)24241,
        [(ushort)24242] = (ushort)24246,
        [(ushort)24243] = (ushort)24247,
        [(ushort)24244] = (ushort)24248,
        [(ushort)24245] = (ushort)24249,
        [(ushort)24250] = (ushort)24254,
        [(ushort)24251] = (ushort)24255,
        [(ushort)24252] = (ushort)24256,
        [(ushort)24253] = (ushort)24257,
        [(ushort)24258] = (ushort)24262,
        [(ushort)24259] = (ushort)24263,
        [(ushort)24260] = (ushort)24264,
        [(ushort)24261] = (ushort)24265,
        [(ushort)24266] = (ushort)24270,
        [(ushort)24267] = (ushort)24271,
        [(ushort)24268] = (ushort)24272,
        [(ushort)24269] = (ushort)24273,
        [(ushort)24274] = (ushort)24278,
        [(ushort)24275] = (ushort)24279,
        [(ushort)24276] = (ushort)24280,
        [(ushort)24277] = (ushort)24281,
        [(ushort)24282] = (ushort)24286,
        [(ushort)24283] = (ushort)24287,
        [(ushort)24284] = (ushort)24288,
        [(ushort)24285] = (ushort)24289,
        [(ushort)24290] = (ushort)24294,
        [(ushort)24291] = (ushort)24295,
        [(ushort)24292] = (ushort)24296,
        [(ushort)24293] = (ushort)24297,
    };
    private static readonly Dictionary<ushort, ushort> Relight = new()
    {
        [(ushort)24239] = (ushort)24235,
        [(ushort)24241] = (ushort)24237,
        [(ushort)24247] = (ushort)24243,
        [(ushort)24249] = (ushort)24245,
        [(ushort)24255] = (ushort)24251,
        [(ushort)24257] = (ushort)24253,
        [(ushort)24263] = (ushort)24259,
        [(ushort)24265] = (ushort)24261,
        [(ushort)24271] = (ushort)24267,
        [(ushort)24273] = (ushort)24269,
        [(ushort)24279] = (ushort)24275,
        [(ushort)24281] = (ushort)24277,
        [(ushort)24287] = (ushort)24283,
        [(ushort)24289] = (ushort)24285,
        [(ushort)24295] = (ushort)24291,
        [(ushort)24297] = (ushort)24293,
    };
    private const int PumpkinState = 10066;
    private const int CarvedPumpkinState = 8689;
    private const int FireState = 4931;

    private static bool Lookup(StateRange[] rules, ushort source, out ushort target)
    {
        foreach (var rule in rules)
            if (rule.TryMap(source, out target)) return true;
        target = 0;
        return false;
    }

    /// <summary>Compute a single right-click action, never mutate the world.
    /// The caller must validate reach/permissions and commit the world edit
    /// before consuming durability or spawning optional seed drops.</summary>
    public static bool TryUse(string identifier, ushort block, int face,
        bool aboveAir, bool adjacentAir, out ToolUse action)
    {
        action = default;
        if (face is < 0 or > 5 || block == 0) return false;
        if (identifier.EndsWith("_axe", StringComparison.Ordinal) &&
            (Lookup(Unwax, block, out ushort target) ||
             Lookup(Scrape, block, out target) ||
             Lookup(Strip, block, out target)))
        {
            action = new ToolUse(target, false);
            return true;
        }
        if (identifier.EndsWith("_hoe", StringComparison.Ordinal) &&
            face != 0 && aboveAir && Lookup(Till, block, out ushort farmland))
        {
            action = new ToolUse(farmland, false);
            return true;
        }
        if (identifier.EndsWith("_shovel", StringComparison.Ordinal))
        {
            if (Extinguish.TryGetValue(block, out ushort extinguished))
            {
                action = new ToolUse(extinguished, false);
                return true;
            }
            if (face != 0 && aboveAir && Lookup(Path, block, out ushort path))
            {
                action = new ToolUse(path, false);
                return true;
            }
        }
        if (identifier == "minecraft:shears" && block == PumpkinState)
        {
            int facing = face switch
            {
                3 => 1, 4 => 2, 5 => 3, _ => 0
            };
            action = new ToolUse((ushort)(CarvedPumpkinState + facing),
                false, Seeds: 4);
            return true;
        }
        if (identifier == "minecraft:flint_and_steel")
        {
            if (Relight.TryGetValue(block, out ushort lit))
            {
                action = new ToolUse(lit, false);
                return true;
            }
            if (adjacentAir)
            {
                action = new ToolUse((ushort)FireState, true);
                return true;
            }
        }
        return false;
    }
}
