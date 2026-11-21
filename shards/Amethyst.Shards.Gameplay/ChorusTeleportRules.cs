using Amethyst.Shards;

namespace Amethyst;

/// <summary>Conservative server-authoritative chorus fruit landing checks.
/// The player must fit completely above a known solid block; dangerous or
/// unsupported block types are rejected rather than guessing collision.</summary>
public static class ChorusTeleportRules
{
    public const int Attempts = 16;
    private const double WorldLimit = 29_999_984;

    public static IEnumerable<PlayerPose> Candidates(PlayerPose origin,
        int minY, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y) ||
            !double.IsFinite(origin.Z) || Math.Abs(origin.X) > WorldLimit ||
            Math.Abs(origin.Z) > WorldLimit)
            yield break;
        for (int i = 0; i < Attempts; i++)
        {
            double x = Math.Floor(origin.X) + random.Next(-8, 9) + 0.5;
            double z = Math.Floor(origin.Z) + random.Next(-8, 9) + 0.5;
            if (Math.Abs(x) > WorldLimit || Math.Abs(z) > WorldLimit)
                continue;
            double y = Math.Clamp(Math.Floor(origin.Y) + random.Next(-8, 9),
                minY + 1, 318);
            yield return new PlayerPose(x, y, z, origin.Yaw, origin.Pitch, true);
        }
    }

    public static bool TryFindLanding(IShardBlockWorld blocks,
        IShardVanillaBlockStates states, PlayerPose candidate, int minY,
        out PlayerPose landing)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(states);
        landing = default;
        if (!double.IsFinite(candidate.X) || !double.IsFinite(candidate.Y) ||
            !double.IsFinite(candidate.Z) || Math.Abs(candidate.X) > WorldLimit ||
            Math.Abs(candidate.Z) > WorldLimit ||
            candidate.Y < minY + 1 || candidate.Y > 318)
            return false;
        int x = (int)Math.Floor(candidate.X);
        int z = (int)Math.Floor(candidate.Z);
        int yStart = (int)Math.Floor(candidate.Y);
        int floorY = Math.Max(minY + 1, yStart - 32);
        for (int y = yStart; y >= floorY; y--)
        {
            // Feet and head must both be air; don't enter liquids, partial
            // blocks, or any collision that this conservative model cannot
            // prove safe.
            if (blocks.GetBlockState(x, y, z) != 0 ||
                blocks.GetBlockState(x, y + 1, z) != 0)
                continue;
            ushort beneath = blocks.GetBlockState(x, y - 1, z);
            if (beneath == 0 || !states.TryGet(beneath, out string blockId,
                    out _) || !SafeFooting(blockId))
                continue;
            landing = candidate with { Y = y, OnGround = true };
            return true;
        }
        return false;
    }

    private static bool SafeFooting(string id) =>
        id is "minecraft:stone" or "minecraft:dirt" or
            "minecraft:grass_block" or "minecraft:coarse_dirt" or
            "minecraft:rooted_dirt" or "minecraft:podzol" or
            "minecraft:mycelium" or "minecraft:gravel" or
            "minecraft:sand" or "minecraft:red_sand" or
            "minecraft:deepslate" or "minecraft:tuff" or
            "minecraft:andesite" or "minecraft:diorite" or
            "minecraft:granite" or "minecraft:bedrock" or
            "minecraft:obsidian" or "minecraft:netherrack" or
            "minecraft:basalt" or "minecraft:blackstone" or
            "minecraft:end_stone" or "minecraft:clay" or
            "minecraft:bricks" or "minecraft:stone_bricks" or
            "minecraft:sandstone" or "minecraft:red_sandstone" or
            "minecraft:packed_mud" or "minecraft:moss_block" or
            "minecraft:snow_block" or "minecraft:terracotta" or
            "minecraft:calcite" or "minecraft:prismarine" or
            "minecraft:quartz_block" ||
        id.StartsWith("minecraft:", StringComparison.Ordinal) &&
            (id.EndsWith("_planks", StringComparison.Ordinal) ||
             id.EndsWith("_log", StringComparison.Ordinal) ||
             id.EndsWith("_wood", StringComparison.Ordinal));
}
