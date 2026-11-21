namespace Amethyst.Shards;

/// <summary>Authoritative 26.3 loot table evaluation inputs; only server
/// observations may set the values. This is deliberately independent of
/// Gameplay's entities and item inventory implementation.</summary>
public sealed record ShardVanillaLootContext
{
    public int Fortune { get; init; }
    public int Looting { get; init; }
    public bool SilkTouch { get; init; }
    public bool Shears { get; init; }
    public bool Burning { get; init; }
    public bool FireAspect { get; init; }
    public bool KilledByPlayer { get; init; } = true;
    public bool InOpenWater { get; init; } = true;
    public double Luck { get; init; }
    public string? Biome { get; init; }
    public string? BlockId { get; init; }
    public IReadOnlyDictionary<string, string>? BlockProperties { get; init; }
    public string? EntityColor { get; init; }
    public bool EntitySheared { get; init; }
    public double? ExplosionRadius { get; init; }
    public Random Random { get; init; } = System.Random.Shared;
}

/// <summary>Version-pinned, read-only vanilla data-pack loot table provider.
/// Unsupported 26.3 predicates and functions throw with their exact name,
/// rather than silently forging a non-vanilla result.</summary>
public interface IShardVanillaLootTables
{
    int Count { get; }
    bool HasTable(string id);
    IReadOnlyList<ShardItemDrop> Roll(string id, ShardVanillaLootContext context);
}
