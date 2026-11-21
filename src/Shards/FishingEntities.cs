namespace Amethyst;

public readonly record struct FishingBobber(
    int EntityId, Guid Uuid, Guid Owner, int OwnerEntityId,
    double X, double Y, double Z, double Vx, double Vy, double Vz,
    bool InWater, bool Biting, long BiteAtMs, long BiteUntilMs,
    int AgeTicks)
{
    /// <summary>Seconds deducted from each bite wait by the rod's Lure
    /// Stats, bounded to preserve a positive bite interval.</summary>
    public int WaitReductionSeconds { get; init; }
}

public readonly record struct FishingReel(bool Caught, bool WasInWater,
    FishingBobber Bobber);

