namespace Amethyst;

public readonly record struct GroundItem(
    int EntityId, Guid Uuid, int ItemId, int Count,
    double X, double Y, double Z, double VelocityY, bool OnGround, long CreatedAtMs)
{
    /// <summary>Full authoritative stack. Null/default means an old
    /// ID/count-only drop created before the durability milestone.</summary>
    public ItemStack Data { get; init; }
    public ItemStack ResolvedStack => Data.Count == 0
        ? new ItemStack(ItemId, Count)
        : Data with { Count = Count };
}

