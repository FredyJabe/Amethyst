using System.Text.Json;

namespace Amethyst.BuiltinShards;

/// <summary>Maps Java 26.3 numeric block-state IDs back to block loot table
/// identifiers and exact state properties. Both come from Mojang's pinned
/// reports/blocks.json rather than guessing from item IDs.</summary>
public sealed class VanillaBlockStateLookup :
    Amethyst.Shards.IShardVanillaBlockStates,
    Amethyst.Shards.IShardDefaultBlockStates
{
    private readonly Dictionary<int, (string Id,
        IReadOnlyDictionary<string, string> Properties)> _states = [];
    private readonly Dictionary<string, ushort> _defaults =
        new(StringComparer.Ordinal);
    public int Count => _states.Count;

    public VanillaBlockStateLookup(string reportFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(reportFile));
        foreach (JsonProperty block in doc.RootElement.EnumerateObject())
        {
            if (!block.Value.TryGetProperty("states", out var states) ||
                states.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement entry in states.EnumerateArray())
            {
                int id = entry.GetProperty("id").GetInt32();
                var properties = new Dictionary<string, string>(StringComparer.Ordinal);
                if (entry.TryGetProperty("properties", out var configured))
                    foreach (JsonProperty property in configured.EnumerateObject())
                        properties.Add(property.Name, property.Value.ToString());
                if (!_states.TryAdd(id, (block.Name, properties)))
                    throw new InvalidDataException($"Duplicate 26.3 block-state ID {id}.");
                if (entry.TryGetProperty("default", out var isDefault) &&
                    isDefault.GetBoolean())
                {
                    if (id is < 0 or > ushort.MaxValue ||
                        !_defaults.TryAdd(block.Name, (ushort)id))
                        throw new InvalidDataException(
                            $"Invalid default 26.3 block state {block.Name}.");
                }
            }
        }
        if (!_states.TryGetValue(1, out var stone) ||
            stone.Id != "minecraft:stone")
            throw new InvalidDataException(
                "Block-state report does not match Java Edition 26.3.");
    }

    public bool TryGetDefaultState(string blockId, out ushort state) =>
        _defaults.TryGetValue(blockId, out state);

    public bool TryGet(ushort state,
        out string blockId, out IReadOnlyDictionary<string, string> properties)
    {
        if (_states.TryGetValue(state, out var entry))
        {
            blockId = entry.Id;
            properties = entry.Properties;
            return true;
        }
        blockId = "";
        properties = new Dictionary<string, string>();
        return false;
    }
}
