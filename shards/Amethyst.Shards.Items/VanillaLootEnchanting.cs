using System.Text.Json;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>
/// Level-based loot enchantment selection based on the pinned 26.3
/// enchantment min/max costs, supported-item tags, weights, and conflicts.
/// This intentionally does not introduce new public YAML component names.
/// </summary>
public sealed partial class VanillaLootTables
{
    private sealed record LootEnchantRule(string Identifier, int Weight, int MaxLevel,
        int MinBase, int MinStep, int MaxBase, int MaxStep,
        string[] SupportedItems);

    private readonly object _enchantRulesGate = new();
    private IReadOnlyDictionary<string, LootEnchantRule>? _lootEnchantRules;
    private string _enchantmentDataDirectory = "";

    private IReadOnlyDictionary<string, LootEnchantRule> GetEnchantRules()
    {
        lock (_enchantRulesGate)
        {
            if (_lootEnchantRules is not null) return _lootEnchantRules;
            if (!Directory.Exists(_enchantmentDataDirectory))
                throw new NotSupportedException(
                    "enchant_with_levels needs the pinned 26.3 enchantment data directory.");
            var rules = new Dictionary<string, LootEnchantRule>(StringComparer.Ordinal);
            foreach (string file in Directory.EnumerateFiles(
                _enchantmentDataDirectory, "*.json"))
            {
                string id = "minecraft:" + Path.GetFileNameWithoutExtension(file);
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement json = doc.RootElement;
                (int minBase, int minStep) = ReadCost(json.GetProperty("min_cost"));
                (int maxBase, int maxStep) = ReadCost(json.GetProperty("max_cost"));
                string[] supported = ReadSelectors(json.GetProperty("supported_items"));
                var rule = new LootEnchantRule(id,
                    json.GetProperty("weight").GetInt32(),
                    json.GetProperty("max_level").GetInt32(),
                    minBase, minStep, maxBase, maxStep, supported);
                if (!rules.TryAdd(id, rule))
                    throw new InvalidDataException("Duplicate pinned enchantment " + id);
            }
            if (rules.Count != 43)
                throw new InvalidDataException(
                    "Pinned enchantment data is incomplete (expected 43).");
            return _lootEnchantRules = rules;
        }
    }

    private static (int Base, int Step) ReadCost(JsonElement json) =>
        (json.GetProperty("base").GetInt32(),
            json.GetProperty("per_level_above_first").GetInt32());

    private static string[] ReadSelectors(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.String => [json.GetString()!],
        JsonValueKind.Array => json.EnumerateArray()
            .Select(x => x.GetString()!).ToArray(),
        _ => throw new NotSupportedException("Unknown 26.3 loot selector shape.")
    };

    private bool EnchantmentFitsItem(LootEnchantRule rule, string itemId)
    {
        if (itemId is "minecraft:book" or "minecraft:enchanted_book")
            return true;
        foreach (string selector in rule.SupportedItems)
        {
            if (selector.StartsWith("#", StringComparison.Ordinal)
                ? ReadTag("item", selector).Contains(itemId, StringComparer.Ordinal)
                : selector == itemId)
                return true;
        }
        return false;
    }

    private static int VanillaEnchantability(ShardItemDefinition item)
    {
        // A configured, validated enchantable component takes precedence.
        if (item.Components is not null &&
            item.Components.TryGetValue("minecraft:enchantable", out string? encoded))
        {
            using var json = JsonDocument.Parse(encoded);
            if (json.RootElement.ValueKind == JsonValueKind.Number)
                return Math.Clamp(json.RootElement.GetInt32(), 0, 100);
        }
        string name = item.Identifier;
        if (name is "minecraft:book" or "minecraft:enchanted_book" or
            "minecraft:bow" or "minecraft:crossbow" or
            "minecraft:fishing_rod" or "minecraft:trident" or
            "minecraft:carrot_on_a_stick" or "minecraft:warped_fungus_on_a_stick")
            return 1;
        if (name is "minecraft:mace") return 15;
        if (name is "minecraft:turtle_helmet") return 9;
        bool tool = name.EndsWith("_sword", StringComparison.Ordinal) ||
            name.EndsWith("_axe", StringComparison.Ordinal) ||
            name.EndsWith("_pickaxe", StringComparison.Ordinal) ||
            name.EndsWith("_shovel", StringComparison.Ordinal) ||
            name.EndsWith("_hoe", StringComparison.Ordinal) ||
            name.EndsWith("_spear", StringComparison.Ordinal);
        bool armor = name.EndsWith("_helmet", StringComparison.Ordinal) ||
            name.EndsWith("_chestplate", StringComparison.Ordinal) ||
            name.EndsWith("_leggings", StringComparison.Ordinal) ||
            name.EndsWith("_boots", StringComparison.Ordinal);
        if (!tool && !armor) return 0;
        if (name.Contains("netherite_", StringComparison.Ordinal)) return 15;
        if (name.Contains("diamond_", StringComparison.Ordinal)) return 10;
        if (name.Contains("golden_", StringComparison.Ordinal)) return armor ? 25 : 22;
        if (name.Contains("iron_", StringComparison.Ordinal)) return armor ? 9 : 14;
        if (name.Contains("chainmail_", StringComparison.Ordinal)) return 12;
        if (name.Contains("leather_", StringComparison.Ordinal)) return 15;
        if (name.Contains("stone_", StringComparison.Ordinal)) return 5;
        if (name.Contains("wooden_", StringComparison.Ordinal)) return 15;
        if (name.Contains("copper_", StringComparison.Ordinal)) return armor ? 8 : 13;
        return 0;
    }

    private static int SelectWeighted(Random random,
        IReadOnlyList<(LootEnchantRule Rule, int Level)> eligible)
    {
        int total = eligible.Sum(x => x.Rule.Weight);
        if (total <= 0)
            throw new InvalidDataException("No weighted loot enchantments.");
        int roll = random.Next(total);
        for (int i = 0; i < eligible.Count; i++)
        {
            roll -= eligible[i].Rule.Weight;
            if (roll < 0) return i;
        }
        return eligible.Count - 1;
    }

    private void EnchantWithLevels(JsonElement modifier,
        ShardVanillaLootContext context, List<Drop> drops)
    {
        if (_enchantments is null)
            throw new NotSupportedException(
                "enchant_with_levels needs the enchantment registry.");
        var rules = GetEnchantRules();
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (modifier.TryGetProperty("options", out var options))
            foreach (string selector in ReadSelectors(options))
                AddOption(selector, allowed);
        else
            AddOption("#minecraft:in_enchanting_table", allowed);

        int baseLevel = (int)Math.Clamp(
            Math.Floor(Sample(modifier.GetProperty("levels"), context)), 1, 255);
        foreach (Drop drop in drops)
        {
            if (!_items.TryGetById(drop.ItemId, out var material)) continue;
            int enchantability = VanillaEnchantability(material);
            if (enchantability <= 0) continue;

            int variance = enchantability / 4 + 1;
            int level = baseLevel + 1 + context.Random.Next(variance) +
                context.Random.Next(variance);
            double multiplier = 1 + (context.Random.NextDouble() +
                context.Random.NextDouble() - 1) * 0.15;
            level = Math.Max(1, (int)Math.Round(level * multiplier));

            var chosen = new Dictionary<string, int>(StringComparer.Ordinal);
            int remainingLevel = level;
            for (int iteration = 0; iteration < 16; iteration++)
            {
                var eligible = new List<(LootEnchantRule Rule, int Level)>();
                foreach (string id in allowed.Order(StringComparer.Ordinal))
                {
                    if (!rules.TryGetValue(id, out var rule) ||
                        rule.Weight <= 0 || !_enchantments.TryGet(id, out _) ||
                        !EnchantmentFitsItem(rule, material.Identifier) ||
                        chosen.Keys.Any(picked =>
                            _enchantments.ConflictsWith(picked, id)))
                        continue;
                    for (int rank = rule.MaxLevel; rank >= 1; rank--)
                    {
                        int min = rule.MinBase + (rank - 1) * rule.MinStep;
                        int max = rule.MaxBase + (rank - 1) * rule.MaxStep;
                        if (remainingLevel < min || remainingLevel > max) continue;
                        eligible.Add((rule, rank));
                        break;
                    }
                }
                if (eligible.Count == 0) break;
                var selected = eligible[SelectWeighted(context.Random, eligible)];
                chosen.Add(selected.Rule.Identifier, selected.Level);
                if (context.Random.Next(50) > remainingLevel) break;
                remainingLevel /= 2;
            }
            if (chosen.Count != 0) ApplyEnchantments(drop, chosen);
        }
    }
}
