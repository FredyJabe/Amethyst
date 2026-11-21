using Amethyst;

namespace Amethyst.Shards;

/// <summary>World livestock owned by the Entities Shard. Gameplay may query
/// and request changes but must not maintain a second entity database.</summary>
public interface IShardLivingEntities
{
    LivingEntity[] Snapshot();
    bool TryGet(int entityId, out LivingEntity entity);
    bool TrySpawnSheep(double x, double y, double z,
        out LivingEntity created, int color = 0);
    bool TryDamage(int entityId, float points,
        out LivingEntity updated, out bool killed);
    bool TryShear(int entityId, out LivingEntity changed);
    LivingEntity[] Regrow(long nowMs, int requiredMs = 180_000);
    bool TryRemove(int entityId);
    bool TryTeleport(int entityId, double x, double y, double z);
    void Save();
}
