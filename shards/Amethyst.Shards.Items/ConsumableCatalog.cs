using System.Collections.ObjectModel;
using Amethyst.Shards;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Amethyst.BuiltinShards;

/// <summary>Configurable, validated server-side use definitions, separate
/// from the corresponding Java network components. A root override replaces
/// the whole entry so disabling/changing a food never mixes old fields.</summary>
public sealed class ConsumableCatalog : IShardConsumableRegistry
{
    private readonly IReadOnlyDictionary<int, ShardConsumableDefinition> _entries;
    public int Count => _entries.Count;
    public bool TryGet(int id, out ShardConsumableDefinition definition) =>
        _entries.TryGetValue(id, out definition);

    public ConsumableCatalog(IShardItemRegistry items, string bundledFile,
        string? overridesDirectory = null)
    {
        var entries = new Dictionary<int, ShardConsumableDefinition>();
        foreach (var (id, def) in Read(items, bundledFile))
        {
            if (!entries.TryAdd(id, def))
                throw new InvalidDataException("Duplicate bundled consumable.");
        }
        if (overridesDirectory is not null)
        {
            Directory.CreateDirectory(overridesDirectory);
            var seen = new HashSet<int>();
            foreach (string file in Directory.EnumerateFiles(overridesDirectory, "*",
                SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
            foreach (var (id, def) in Read(items, file))
            {
                if (!seen.Add(id))
                    throw new InvalidDataException(
                        $"Consumable for protocol item {id} overridden twice.");
                entries[id] = def;
            }
        }
        _entries = new ReadOnlyDictionary<int, ShardConsumableDefinition>(entries);
    }

    private static IEnumerable<(int Id, ShardConsumableDefinition Definition)>
        Read(IShardItemRegistry items, string path)
    {
        var stream = new YamlStream();
        try
        {
            using var input = File.OpenText(path);
            stream.Load(input);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"Invalid consumables YAML {path}", ex);
        }
        if (stream.Documents.Count != 1 ||
            stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException($"Consumables must be a root mapping: {path}");
        var owners = new HashSet<int>();
        foreach (var entry in root.Children)
        {
            string name = Scalar(entry.Key);
            if (!name.StartsWith("minecraft:", StringComparison.Ordinal) ||
                !items.TryGetByIdentifier(name, out var item) ||
                item.ProtocolId <= 0 || !owners.Add(item.ProtocolId) ||
                entry.Value is not YamlMappingNode options)
                throw new InvalidDataException($"Invalid consumable root {name} in {path}.");
            int? nutrition = null, ticks = null;
            float? saturation = null;
            bool always = false;
            int remainderId = 0;
            var effects = new List<ShardConsumeEffect>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in options.Children)
            {
                string key = Scalar(field.Key);
                if (!used.Add(key))
                    throw new InvalidDataException($"Repeated consumable field {key} in {name}.");
                switch (key)
                {
                    case "Nutrition": nutrition = Integer(field.Value, 0, 20); break;
                    case "Saturation":
                        if (!float.TryParse(Scalar(field.Value),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out float sat) || !float.IsFinite(sat) ||
                            sat is < 0 or > 40)
                            throw new InvalidDataException($"Invalid saturation in {name}.");
                        saturation = sat;
                        break;
                    case "DurationTicks": ticks = Integer(field.Value, 1, 2400); break;
                    case "AlwaysEdible":
                        if (!bool.TryParse(Scalar(field.Value), out always))
                            throw new InvalidDataException($"Invalid AlwaysEdible in {name}.");
                        break;
                    case "Remainder":
                        string remainder = Scalar(field.Value);
                        if (!remainder.StartsWith("minecraft:", StringComparison.Ordinal) ||
                            !items.TryGetByIdentifier(remainder, out var remaining) ||
                            remaining.ProtocolId == 0)
                            throw new InvalidDataException($"Unknown remainder {remainder}.");
                        remainderId = remaining.ProtocolId;
                        break;
                    case "Effects":
                        if (field.Value is not YamlSequenceNode effectsNode ||
                            effectsNode.Children.Count > 16)
                            throw new InvalidDataException($"Invalid Effects list for {name}.");
                        foreach (var child in effectsNode.Children)
                            effects.Add(ParseEffect(child, name));
                        break;
                    default: throw new InvalidDataException(
                        $"Unsupported consumable property '{key}' for {name}.");
                }
            }
            if (nutrition is null || saturation is null || ticks is null)
                throw new InvalidDataException(
                    $"Missing Nutrition, Saturation or DurationTicks for {name}.");
            yield return (item.ProtocolId, new ShardConsumableDefinition(
                item.ProtocolId, nutrition.Value, saturation.Value,
                ticks.Value, always, remainderId)
                { Effects = Array.AsReadOnly(effects.ToArray()) });
        }
    }

    private static ShardConsumeEffect ParseEffect(YamlNode node, string owner)
    {
        if (node is not YamlMappingNode map)
            throw new InvalidDataException($"Invalid consumable effect for {owner}.");
        string? id = null;
        int duration = 0, amplifier = 0;
        double chance = 1;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in map.Children)
        {
            string key = Scalar(pair.Key);
            if (!keys.Add(key)) throw new InvalidDataException("Repeated consume effect key.");
            switch (key)
            {
                case "Id": id = Scalar(pair.Value); break;
                case "DurationTicks": duration = Integer(pair.Value, 0, 72000); break;
                case "Amplifier": amplifier = Integer(pair.Value, 0, 255); break;
                case "Chance":
                    if (!double.TryParse(Scalar(pair.Value),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out chance) || !double.IsFinite(chance) ||
                        chance is < 0 or > 1)
                        throw new InvalidDataException("Invalid consume effect chance.");
                    break;
                default: throw new InvalidDataException(
                    $"Unknown consumable effect property {key}.");
            }
        }
        if (id is null || !id.StartsWith("minecraft:", StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid consume effect ID for {owner}.");
        return new ShardConsumeEffect(id, duration, amplifier, chance);
    }

    private static int Integer(YamlNode node, int min, int max)
    {
        if (!int.TryParse(Scalar(node),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int number) || number < min || number > max)
            throw new InvalidDataException("Consumable integer is outside valid range.");
        return number;
    }

    private static string Scalar(YamlNode node) =>
        node is YamlScalarNode { Value: { } value } ? value :
        throw new InvalidDataException("Expected scalar consumable YAML value.");
}
