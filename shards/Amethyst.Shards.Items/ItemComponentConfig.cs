using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amethyst.Shards;
using YamlDotNet.RepresentationModel;

namespace Amethyst.BuiltinShards;

/// <summary>Strict, version-pinned YAML adapters for the subset of Java 26.3
/// item components Amethyst can actually encode on the wire. Values cross the
/// Shard API as canonical JSON, with no dependency on YamlDotNet in Core.</summary>
internal static class ItemComponentConfig
{
    private static readonly Regex IdentifierPattern = new(
        @"^[a-z0-9_.-]+:[a-z0-9_./-]+$", RegexOptions.Compiled |
        RegexOptions.CultureInvariant);

    public static string Key(string key)
    {
        string normalized = key.Trim().ToLowerInvariant();
        return normalized.Contains(':') ? normalized : "minecraft:" + normalized;
    }

    public static string Identifier(YamlNode node, string context)
    {
        string value = Scalar(node, context).Trim().ToLowerInvariant();
        if (!value.Contains(':')) value = "minecraft:" + value;
        if (value.Length > 256 || !IdentifierPattern.IsMatch(value))
            throw new InvalidDataException($"Invalid component identifier '{value}' in {context}.");
        return value;
    }

    public static IReadOnlyDictionary<string, string> Parse(
        YamlNode node, string context, ItemComponentNodeMap? nodes = null)
    {
        if (node is not YamlMappingNode mapping)
            throw new InvalidDataException($"Components in {context} must be a mapping.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        nodes ??= new ItemComponentNodeMap();
        foreach (var pair in mapping.Children)
        {
            string nodeName = Scalar(pair.Key, context);
            if (!nodes.TryResolve(nodeName, out string key))
                throw new InvalidDataException(
                    $"Unknown or unassigned YAML component node '{nodeName}' in {context}. " +
                    "Use one of Amethyst's fixed node names or an explicit minecraft: ID.");
            if (!result.TryAdd(key, Encode(key, pair.Value, context)))
                throw new InvalidDataException($"Duplicate component {key} in {context}.");
        }
        return new ReadOnlyDictionary<string, string>(result);
    }

    public static string Encode(string key, YamlNode node, string context)
    {
        string at = $"{key} in {context}";
        if (!ShardItemComponentIds.TryGetId(key, out _))
            throw new InvalidDataException($"Unknown component '{key}' in {context}.");
        // The registry contains 122 independently typed network codecs.
        // Advanced raw bytes permit a correct vanilla-encoded payload for any
        // type even before Amethyst has a first-class structured serializer.
        // No unsafe auto-encoding of arbitrary YAML into the wrong wire type.
        if (node is YamlMappingNode raw)
        {
            var entries = raw.Children.ToArray();
            if (entries.Length == 1 &&
                entries[0].Key is YamlScalarNode { Value: { } rawKey })
            {
                if (rawKey.Equals("WireHex", StringComparison.OrdinalIgnoreCase))
                {
                    string hex = Scalar(entries[0].Value, at).Trim();
                    if (hex.Length > 131072 || (hex.Length % 2) != 0 ||
                        hex.Any(ch => !Uri.IsHexDigit(ch)))
                        throw new InvalidDataException(
                            $"WireHex in {at} must contain at most 65536 bytes of hex.");
                    return JsonSerializer.Serialize(new { WireHex = hex.ToUpperInvariant() });
                }
                if (rawKey.Equals("Remove", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Boolean(entries[0].Value, at))
                        throw new InvalidDataException(
                            $"Remove must be true in {at}.");
                    return JsonSerializer.Serialize(new { Remove = true });
                }
            }
        }
        return key switch
        {
            "minecraft:item_model" or "minecraft:tooltip_style" =>
                JsonSerializer.Serialize(Identifier(node, at)),
            "minecraft:custom_name" or "minecraft:item_name" =>
                JsonSerializer.Serialize(Text(node, at)),
            "minecraft:lore" => JsonSerializer.Serialize(
                Sequence(node, at, Text, 64)),
            "minecraft:rarity" => JsonSerializer.Serialize(Rarity(node, at)),
            "minecraft:enchantment_glint_override" or "minecraft:unbreakable" =>
                JsonSerializer.Serialize(Boolean(node, at)),
            "minecraft:repair_cost" or "minecraft:damage" or
            "minecraft:max_damage" or "minecraft:enchantable" or
            "minecraft:map_id" => JsonSerializer.Serialize(
                Integer(node, at, 0, int.MaxValue)),
            "minecraft:dyed_color" => JsonSerializer.Serialize(
                Integer(node, at, 0, 0xFFFFFF)),
            "minecraft:creative_slot_lock" or "minecraft:intangible_projectile" or
            "minecraft:glider" or "minecraft:waxed" => JsonSerializer.Serialize(
                Boolean(node, at)),
            "minecraft:max_stack_size" => JsonSerializer.Serialize(
                Integer(node, at, 1, 64)),
            "minecraft:custom_model_data" => CustomModelData(node, at),
            "minecraft:tooltip_display" => TooltipDisplay(node, at),
            _ => throw new InvalidDataException(
                $"Component '{key}' needs its Java 26.3 encoded payload in {at}. " +
                "Use { WireHex: '...' } to supply bytes or { Remove: true } " +
                "to remove the component from the vanilla item defaults.")
        };
    }

    private static string Rarity(YamlNode node, string context)
    {
        string value = Scalar(node, context).Trim().ToLowerInvariant();
        if (value is not ("common" or "uncommon" or "rare" or "epic"))
            throw new InvalidDataException($"Unsupported rarity '{value}' in {context}.");
        return value;
    }

    private static string CustomModelData(YamlNode node, string context)
    {
        if (node is YamlScalarNode)
        {
            int oldValue = Integer(node, context, 0, 16777216);
            return JsonSerializer.Serialize(new {
                floats = new float[] { oldValue }, flags = Array.Empty<bool>(),
                strings = Array.Empty<string>(), colors = Array.Empty<int>()
            });
        }
        if (node is not YamlMappingNode map)
            throw new InvalidDataException($"{context} must be an integer or a mapping.");
        var floats = Array.Empty<float>();
        var flags = Array.Empty<bool>();
        var strings = Array.Empty<string>();
        var colors = Array.Empty<int>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in map.Children)
        {
            string key = Scalar(pair.Key, context).ToLowerInvariant();
            if (!seen.Add(key)) throw new InvalidDataException($"Duplicate {key} in {context}.");
            switch (key)
            {
                case "floats": floats = Sequence(pair.Value, context, Float, 64); break;
                case "flags": flags = Sequence(pair.Value, context, Boolean, 64); break;
                case "strings": strings = Sequence(pair.Value, context, Text, 64); break;
                case "colors": colors = Sequence(pair.Value, context,
                    (n, c) => Integer(n, c, 0, 0xFFFFFF), 64); break;
                default: throw new InvalidDataException($"Unsupported {context}.{key}.");
            }
        }
        return JsonSerializer.Serialize(new { floats, flags, strings, colors });
    }

    private static string TooltipDisplay(YamlNode node, string context)
    {
        if (node is not YamlMappingNode map)
            throw new InvalidDataException($"{context} must be a mapping.");
        bool hide = false;
        var components = Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in map.Children)
        {
            string key = Scalar(pair.Key, context).ToLowerInvariant();
            if (!seen.Add(key)) throw new InvalidDataException($"Duplicate {key} in {context}.");
            switch (key)
            {
                case "hide_tooltip": hide = Boolean(pair.Value, context); break;
                case "hidden_components":
                    components = Sequence(pair.Value, context, (n, c) =>
                    {
                        string id = Key(Scalar(n, c));
                        if (!ShardItemComponentIds.TryGetId(id, out _))
                            throw new InvalidDataException(
                                $"Unknown hidden component '{id}' in {c}.");
                        return id;
                    }, 64);
                    if (components.Distinct(StringComparer.Ordinal).Count() != components.Length)
                        throw new InvalidDataException($"Repeated hidden component in {context}.");
                    break;
                default: throw new InvalidDataException($"Unsupported {context}.{key}.");
            }
        }
        return JsonSerializer.Serialize(new { hide_tooltip = hide,
            hidden_components = components });
    }

    private static string Scalar(YamlNode node, string context) =>
        node is YamlScalarNode { Value: { } value } ? value :
        throw new InvalidDataException($"Expected scalar in {context}.");

    private static string Text(YamlNode node, string context)
    {
        string text = Scalar(node, context);
        if (text.Length > 1024)
            throw new InvalidDataException($"Text exceeds 1024 chars in {context}.");
        return text;
    }

    private static int Integer(YamlNode node, string context, int min, int max)
    {
        string s = Scalar(node, context);
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            || number < min || number > max)
            throw new InvalidDataException($"Expected integer {min}..{max} in {context}.");
        return number;
    }

    private static float Float(YamlNode node, string context)
    {
        if (!float.TryParse(Scalar(node, context), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float result) ||
            !float.IsFinite(result))
            throw new InvalidDataException($"Invalid finite float in {context}.");
        return result;
    }

    private static bool Boolean(YamlNode node, string context)
    {
        if (!bool.TryParse(Scalar(node, context), out bool result))
            throw new InvalidDataException($"Invalid boolean in {context}.");
        return result;
    }

    private static T[] Sequence<T>(YamlNode node, string context,
        Func<YamlNode, string, T> parse, int limit)
    {
        if (node is not YamlSequenceNode seq || seq.Children.Count > limit)
            throw new InvalidDataException($"Expected sequence (max {limit}) in {context}.");
        return seq.Children.Select(child => parse(child, context)).ToArray();
    }
}
