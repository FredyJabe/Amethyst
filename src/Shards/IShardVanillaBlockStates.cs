namespace Amethyst.Shards;

/// <summary>Exact Java 26.3 block-state registry lookup. State IDs must be
/// resolved before evaluating vanilla block loot predicates.</summary>
public interface IShardVanillaBlockStates
{
    int Count { get; }
    bool TryGet(ushort state, out string blockId,
        out IReadOnlyDictionary<string, string> properties);
}


/// <summary>Optional Items-owned inverse lookup for vanilla default block
/// states. Gameplay uses this when it must author a new block such as source
/// water or lava without embedding version-dependent numeric IDs.</summary>
public interface IShardDefaultBlockStates
{
    bool TryGetDefaultState(string blockId, out ushort state);
}
