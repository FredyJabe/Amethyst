namespace Amethyst;

/// <summary>Persistent livestock state owned by the Entities Shard. Gameplay
/// uses this immutable snapshot for hit testing and client metadata only.</summary>
public readonly record struct LivingEntity(int EntityId, Guid Uuid,
    string Kind, double X, double Y, double Z, float Health,
    bool Sheared, int Color, long LastGrazeAt)
{
    public bool Alive => Health > 0;
}
