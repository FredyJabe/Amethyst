using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Amethyst.Shards;
using YamlDotNet.RepresentationModel;

namespace Amethyst.BuiltinShards;

/// <summary>Common strict Stats YAML format for items and enchantments.
/// Short list entries ("damage 50") set the event-local value exactly.
/// Advanced stat mappings retain explicit add/multiply/set modifiers and triggers.
/// The YAML stat keys are server-side names, not item component node names.</summary>
public static class StatModifiers
{
    private static readonly Regex StatName = new(
        @"^[a-z][a-z0-9_]{0,63}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TriggerName = new(
        @"^on[a-z0-9_-]{1,63}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<ShardStatModifier> Parse(YamlNode node, string owner)
    {
        var result = new List<ShardStatModifier>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Preferred shorthand:
        // Stats:
        //   - damage 50
        //   - attack_speed 2
        // Numbers are absolute values, not additive bonuses.
        if (node is YamlSequenceNode assignments)
        {
            if (assignments.Children.Count > 64)
                throw new InvalidDataException(
                    $"Stats for '{owner}' can contain at most 64 entries.");
            foreach (YamlNode assignment in assignments.Children)
            {
                string[] parts = Scalar(assignment, owner).Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !StatName.IsMatch(parts[0]) ||
                    !names.Add(parts[0]))
                    throw new InvalidDataException(
                        $"Invalid or duplicate Stats assignment in '{owner}': expected '- stat_name number'.");
                result.Add(new ShardStatModifier(parts[0].ToLowerInvariant(),
                    Number(parts[1], owner), 0, "set", null, []));
            }
            return Array.AsReadOnly(result.ToArray());
        }

        // Keep the advanced mapping format for vanilla enchantment formulas
        // and compatibility with existing administrator definitions.
        if (node is not YamlMappingNode map || map.Children.Count > 64)
            throw new InvalidDataException(
                $"Stats for '{owner}' must be a list or mapping (max 64 entries).");
        foreach (var entry in map.Children)
        {
            string stat = Scalar(entry.Key, owner);
            if (!StatName.IsMatch(stat) || !names.Add(stat))
                throw new InvalidDataException($"Invalid or duplicate Stats key '{stat}' for '{owner}'.");
            if (entry.Value is YamlSequenceNode sequence)
            {
                if (sequence.Children.Count is < 1 or > 16)
                    throw new InvalidDataException($"Stats.{stat} needs 1..16 modifiers ({owner}).");
                foreach (YamlNode child in sequence.Children)
                    result.Add(ParseModifier(stat, child, owner));
            }
            else result.Add(ParseModifier(stat, entry.Value, owner));
            if (result.Count > 128)
                throw new InvalidDataException($"Too many stat modifiers for '{owner}'.");
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static ShardStatModifier ParseModifier(string stat, YamlNode node, string owner)
    {
        if (node is YamlScalarNode)
            return new ShardStatModifier(stat.ToLowerInvariant(),
                Number(node, owner), 0, "set", null, []);
        if (node is not YamlMappingNode map)
            throw new InvalidDataException($"Stats.{stat} for '{owner}' must be a number or mapping.");

        double basis = 0, perLevel = 0;
        bool hasAmount = false;
        string operation = "add";
        string? trigger = null;
        IReadOnlyList<ShardSkillCondition> conditions = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in map.Children)
        {
            string field = Scalar(pair.Key, owner);
            if (!seen.Add(field))
                throw new InvalidDataException($"Duplicate Stats.{stat}.{field} for '{owner}'.");
            switch (field.ToLowerInvariant())
            {
                case "base":
                    basis = Number(pair.Value, owner); hasAmount = true; break;
                case "perlevel":
                    perLevel = Number(pair.Value, owner); hasAmount = true; break;
                case "operation":
                    operation = Scalar(pair.Value, owner).ToLowerInvariant();
                    if (operation is not ("add" or "multiply" or "set"))
                        throw new InvalidDataException($"Invalid Stats.{stat}.Operation for '{owner}'.");
                    break;
                case "trigger":
                    trigger = Scalar(pair.Value, owner).Trim().TrimStart('~');
                    if (!TriggerName.IsMatch(trigger))
                        throw new InvalidDataException($"Invalid Stats.{stat}.Trigger for '{owner}'.");
                    break;
                case "conditions":
                    if (pair.Value is not YamlSequenceNode list ||
                        list.Children.Count > 16)
                        throw new InvalidDataException(
                            $"Stats.{stat}.Conditions must be a list of at most 16 conditions ({owner}).");
                    var parsed = new List<ShardSkillCondition>();
                    foreach (YamlNode entry in list.Children)
                    {
                        string expression = Scalar(entry, owner).Trim().TrimStart('?');
                        var skill = SkillEngine.Parse(owner,
                            $"setFlag @self ~onAttack ?{expression}");
                        if (skill.Conditions.Count != 1)
                            throw new InvalidDataException(
                                $"Invalid Stats.{stat} condition for '{owner}'.");
                        parsed.Add(skill.Conditions[0]);
                    }
                    conditions = Array.AsReadOnly(parsed.ToArray());
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unknown Stats.{stat}.{field} for '{owner}'.");
            }
        }
        if (!hasAmount)
            throw new InvalidDataException($"Stats.{stat} for '{owner}' needs Base or PerLevel.");
        return new ShardStatModifier(stat.ToLowerInvariant(), basis, perLevel,
            operation, trigger, conditions);
    }

    private static string Scalar(YamlNode node, string owner) =>
        node is YamlScalarNode { Value: { } value } ? value :
        throw new InvalidDataException($"Expected scalar in Stats for '{owner}'.");

    private static double Number(YamlNode node, string owner) =>
        Number(Scalar(node, owner), owner);

    private static double Number(string text, string owner)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double value) || !double.IsFinite(value) ||
            Math.Abs(value) > 1_000_000)
            throw new InvalidDataException($"Invalid Stats number '{text}' for '{owner}'.");
        return value;
    }
}
