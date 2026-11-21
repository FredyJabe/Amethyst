namespace Amethyst.Shards;

/// <summary>Server-authoritative item use behavior, separate from the 26.3
/// network component patch. Duration is the minimum hold time in ticks.</summary>
public readonly record struct ShardConsumableDefinition(
    int ItemId, int Nutrition, float Saturation, int DurationTicks,
    bool AlwaysEdible, int RemainderItemId)
{
    /// <summary>Optional effects resolved after consumption; all timings are
    /// server ticks, not wall-clock seconds.</summary>
    public IReadOnlyList<ShardConsumeEffect> Effects { get; init; } =
        Array.Empty<ShardConsumeEffect>();
}
public readonly record struct ShardConsumeEffect(string Id,
    int DurationTicks, int Amplifier, double Chance);

public interface IShardConsumableRegistry
{
    int Count { get; }
    bool TryGet(int itemId, out ShardConsumableDefinition definition);
}
