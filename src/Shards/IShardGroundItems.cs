using Amethyst;

namespace Amethyst.Shards;

public interface IShardPickupInventory
{
    int GiveStack(ItemStack stack);
}

/// <summary>Physical dropped item state. Its data, physics, lifecycle and
/// persistence belong to the Entities Shard. Gameplay handles pickup input
/// and client packets using this service.</summary>
public interface IShardGroundItems
{
    GroundItem[] Snapshot();
    bool TrySpawn(int itemId, int count, double x, double y, double z,
        out GroundItem entity, long? nowMilliseconds = null);
    bool TrySpawn(ItemStack stack, double x, double y, double z,
        out GroundItem entity, long? nowMilliseconds = null);
    bool TryRemove(int entityId);
    bool TryDiscard(int entityId);
    void Simulate(IShardBlockWorld world, long? nowMilliseconds = null);
    int TryCollect(int entityId, IShardPickupInventory inventory,
        out GroundItem remaining, long? nowMilliseconds = null);
    void Save();
}
