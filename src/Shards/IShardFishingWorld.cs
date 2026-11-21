using Amethyst;

namespace Amethyst.Shards;

/// <summary>Transient physical fishing bobbers simulated and owned by Entities.
/// Gameplay handles client interactions and packets, not bobber state.</summary>
public interface IShardFishingWorld
{
    FishingBobber[] Snapshot();
    bool HasHook(Guid owner);
    bool TryCast(Guid owner, int ownerEntityId, PlayerPose pos,
        out FishingBobber bobber, int waitReductionSeconds = 0);
    bool TryReel(Guid owner, out FishingReel result,
        long? nowMilliseconds = null);
    bool Cancel(Guid owner);
    FishingBobber[] Tick(IShardBlockWorld world,
        long? nowMilliseconds = null, Random? random = null);
    bool CheckOpenWater(IShardBlockWorld world, FishingBobber bobber);
    (string Item, int Xp) RollStarterLoot(Random? random = null);
}
