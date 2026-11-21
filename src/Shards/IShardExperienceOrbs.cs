using Amethyst;

namespace Amethyst.Shards;

/// <summary>Persistent physical XP orbs owned by Entities. Gameplay awards
/// experience only after requesting authoritative pickup from this service.</summary>
public interface IShardExperienceOrbs
{
    ExperienceOrb[] Snapshot();
    bool TrySpawn(int value, double x, double y, double z,
        out ExperienceOrb orb, long? nowMs = null);
    void Simulate(IShardBlockWorld world, long? nowMs = null);
    int TryCollect(int entityId, long? nowMs = null);
    void Save();
}
