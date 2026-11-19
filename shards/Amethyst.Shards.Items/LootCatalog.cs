using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Amethyst.Shards;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Amethyst.BuiltinShards;

/// <summary>Entity loot rules live in Items, not in Entities or Gameplay.
/// Bundled defaults are loaded first; user YAML overrides whole tables by ID.</summary>
public sealed class LootCatalog : IShardLootRegistry
{
    private static readonly Regex EntityKey = new(
        @"^([a-z0-9_.-]+:)?[a-z0-9_./-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<string, ShardLootTable> _tables;
    public IReadOnlyList<ShardLootTable> Definitions { get; }
    public int TableCount => _tables.Count;

    public LootCatalog(IShardItemRegistry items, string defaultsFile,
        string? overridesDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultsFile);
        var tables = new Dictionary<string, ShardLootTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in ReadFile(defaultsFile, items))
        {
            if (!tables.TryAdd(key, value))
                throw new InvalidDataException($"Duplicate bundled loot table '{key}' in {defaultsFile}.");
        }
        if (overridesDirectory is not null)
        {
            Directory.CreateDirectory(overridesDirectory);
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(overridesDirectory, "*",
                SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
                foreach (var (key, value) in ReadFile(file, items))
                {
                    if (!touched.Add(key))
                        throw new InvalidDataException(
                            $"Loot table '{key}' overridden more than once ({file}).");
                    tables[key] = value;
                }
        }
        _tables = new ReadOnlyDictionary<string, ShardLootTable>(tables);
        Definitions = Array.AsReadOnly(tables.Values.OrderBy(t => t.EntityId,
            StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public bool TryRollEntity(string entityId, out ShardLootStack[] drops, Random? random = null)
    {
        drops = [];
        if (string.IsNullOrWhiteSpace(entityId)) return false;
        string key = Normalize(entityId);
        // The short form may identify a Mythic-style alias configured in the
        // Entities Shard. The full name takes precedence when both exist.
        if (!_tables.TryGetValue(key, out var table) &&
            !_tables.TryGetValue(key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key,
                out table))
            return false;
        random ??= Random.Shared;
        var rolled = new List<ShardLootStack>();
        foreach (ShardLootEntry entry in table.Drops)
        {
            if (random.NextDouble() >= entry.Chance) continue;
            int amount = random.Next(entry.Min, entry.Max + 1);
            if (amount <= 0) continue;
            // A table can request >64 items. Split according to the actual
            // vanilla item stack cap (looked up and validated at YAML load).
            int cap = _stackSizes[entry.ItemId];
            while (amount > 0)
            {
                int count = Math.Min(cap, amount);
                rolled.Add(new ShardLootStack(entry.ItemId, count));
                amount -= count;
            }
        }
        drops = rolled.ToArray();
        return true;
    }

    private readonly Dictionary<int, int> _stackSizes = new();

    private static string Normalize(string id)
    {
        string value = id.Trim().ToLowerInvariant();
        return value.StartsWith("minecraft:", StringComparison.Ordinal)
            ? value["minecraft:".Length..] : value;
    }

    private IEnumerable<(string Key, ShardLootTable Value)> ReadFile(
        string path, IShardItemRegistry items)
    {
        var yaml = new YamlStream();
        try
        {
            using var stream = File.OpenText(path);
            yaml.Load(stream);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"Malformed loot YAML '{path}': {ex.Message}", ex);
        }
        if (yaml.Documents.Count == 0) yield break;
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException($"Loot YAML must be a root mapping ({path}).");
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root.Children)
        {
            string id = Scalar(entry.Key, path);
            string key = Normalize(id);
            if (!EntityKey.IsMatch(key) || !roots.Add(key))
                throw new InvalidDataException($"Duplicate or invalid loot ID '{id}' ({path}).");
            if (entry.Value is not YamlMappingNode table)
                throw new InvalidDataException($"Loot table '{id}' must be a mapping ({path}).");
            bool enabled = true;
            YamlSequenceNode? dropNodes = null;
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in table.Children)
            {
                string name = Scalar(pair.Key, path);
                if (!fields.Add(name))
                    throw new InvalidDataException($"Duplicate loot field '{name}' in '{id}' ({path}).");
                switch (name.ToLowerInvariant())
                {
                    case "enabled": enabled = Boolean(pair.Value, path); break;
                    case "drops":
                        dropNodes = pair.Value as YamlSequenceNode ??
                            throw new InvalidDataException(
                                $"Drops must be a sequence in loot '{id}' ({path}).");
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Unsupported loot field '{name}' in '{id}' ({path}).");
                }
            }
            if (enabled && dropNodes is null)
                throw new InvalidDataException($"Loot '{id}' requires a Drops list ({path}).");
            var drops = new List<ShardLootEntry>();
            if (enabled && dropNodes is not null)
            {
                if (dropNodes.Children.Count > 32)
                    throw new InvalidDataException($"Too many drop entries in '{id}' ({path}).");
                foreach (YamlNode node in dropNodes.Children)
                {
                    if (node is not YamlMappingNode d)
                        throw new InvalidDataException($"Loot drop in '{id}' must be a mapping.");
                    string? itemName = null;
                    int min = 1, max = 1;
                    double chance = 1;
                    bool hasMin = false, hasMax = false;
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in d.Children)
                    {
                        string name = Scalar(property.Key, path);
                        if (!seen.Add(name))
                            throw new InvalidDataException($"Duplicate drop field '{name}' ({path}).");
                        switch (name.ToLowerInvariant())
                        {
                            case "item": itemName = Scalar(property.Value, path); break;
                            case "min": min = Integer(property.Value, path); hasMin = true; break;
                            case "max": max = Integer(property.Value, path); hasMax = true; break;
                            case "chance": chance = Number(property.Value, path); break;
                            default:
                                throw new InvalidDataException(
                                    $"Unsupported drop field '{name}' in loot '{id}' ({path}).");
                        }
                    }
                    if (hasMin && !hasMax) max = min;
                    if (!hasMin && hasMax) min = 0;
                    if (itemName is null || !items.TryGetByIdentifier(itemName, out var item) ||
                        item.ProtocolId == 0)
                        throw new InvalidDataException(
                            $"Unknown or invalid drop item '{itemName}' in loot '{id}' ({path}).");
                    if (min < 0 || max < min || max > 64 ||
                        !double.IsFinite(chance) || chance < 0 || chance > 1)
                        throw new InvalidDataException(
                            $"Invalid min/max/chance in drop for '{id}' ({path}).");
                    _stackSizes[item.ProtocolId] = item.MaxStackSize;
                    drops.Add(new ShardLootEntry(item.ProtocolId, min, max, chance));
                }
            }
            yield return (key, new ShardLootTable(key, Array.AsReadOnly(drops.ToArray())));
        }
    }

    private static string Scalar(YamlNode node, string file) =>
        node is YamlScalarNode { Value: { } text } ? text :
            throw new InvalidDataException($"Expected scalar in loot YAML '{file}'.");
    private static bool Boolean(YamlNode node, string file) =>
        bool.TryParse(Scalar(node, file), out bool value) ? value :
            throw new InvalidDataException($"Invalid boolean in loot YAML '{file}'.");
    private static int Integer(YamlNode node, string file) =>
        int.TryParse(Scalar(node, file), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int value) ? value : throw new InvalidDataException($"Invalid integer in loot YAML '{file}'.");
    private static double Number(YamlNode node, string file) =>
        double.TryParse(Scalar(node, file), NumberStyles.Float, CultureInfo.InvariantCulture,
            out double value) && double.IsFinite(value) ? value :
            throw new InvalidDataException($"Invalid chance in loot YAML '{file}'.");
}
