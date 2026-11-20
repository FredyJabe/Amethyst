using System.Collections.ObjectModel;
using System.Globalization;
using Amethyst.Shards;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Amethyst.BuiltinShards;

/// <summary>Validated vanilla-enchantment catalog with per-enchantment
/// administrator overrides and shared core skill instructions. The client
/// registry is not extended by introducing custom YAML roots.</summary>
public sealed class EnchantmentCatalog : IShardEnchantmentRegistry
{
    private sealed class Fields
    {
        public string? Id;
        public int? MaxLevel, Weight, AnvilCost;
        public string[]? SupportedItems, Conflicts, Skills;
        public bool? Treasure, Curse;
    }

    private readonly Dictionary<string, ShardEnchantmentDefinition> _byId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<ShardEnchantmentDefinition> _all;
    public int Count => _all.Count;
    public IReadOnlyList<ShardEnchantmentDefinition> All => _all;
    public IEnumerable<ShardSkillInstruction> Instructions => _all.SelectMany(e => e.Skills);

    public EnchantmentCatalog(string bundledPath, string? overrideDirectory = null)
    {
        foreach (var (id, fields) in ReadEntries(bundledPath))
        {
            if (fields.Id is null || fields.MaxLevel is null || fields.Weight is null ||
                fields.AnvilCost is null || fields.SupportedItems is null || fields.Skills is null)
                throw new InvalidDataException($"Incomplete vanilla enchantment '{id}'.");
            // Verify the exact vanilla registry, including the two curses.
            _ = VanillaRegistries.IndexOf("minecraft:enchantment", id);
            if (!_byId.TryAdd(id, Make(id, fields, null)))
                throw new InvalidDataException($"Duplicate vanilla enchantment '{id}'.");
        }
        if (_byId.Count != 43)
            throw new InvalidDataException("Expected 43 Java 26.3 enchantment definitions.");
        if (overrideDirectory is not null)
        {
            Directory.CreateDirectory(overrideDirectory);
            var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(overrideDirectory, "*",
                SearchOption.AllDirectories).Where(f =>
                    f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
            foreach (var (id, fields) in ReadEntries(file))
            {
                if (!updated.Add(id))
                    throw new InvalidDataException($"Enchantment '{id}' overridden twice ({file}).");
                if (!_byId.TryGetValue(id, out var old))
                    throw new InvalidDataException(
                        $"Unsupported enchantment '{id}' in {file}: Java 26.3 registry is fixed.");
                _byId[id] = Make(id, fields, old);
            }
        }
        foreach (var enchantment in _byId.Values)
            foreach (string conflict in enchantment.Conflicts)
                if (!_byId.ContainsKey(conflict) || conflict == enchantment.Identifier)
                    throw new InvalidDataException(
                        $"Invalid enchantment conflict {enchantment.Identifier} -> {conflict}.");
        _all = new ReadOnlyCollection<ShardEnchantmentDefinition>(
            _byId.Values.OrderBy(e =>
                VanillaRegistries.IndexOf("minecraft:enchantment", e.Identifier)).ToArray());
    }

    public bool TryGet(string identifier, out ShardEnchantmentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            definition = null!;
            return false;
        }
        return _byId.TryGetValue(Normalize(identifier), out definition!);
    }

    public bool ConflictsWith(string first, string second)
    {
        if (!TryGet(first, out var a) || !TryGet(second, out var b)) return true;
        return a.Identifier == b.Identifier ||
            a.Conflicts.Contains(b.Identifier, StringComparer.OrdinalIgnoreCase) ||
            b.Conflicts.Contains(a.Identifier, StringComparer.OrdinalIgnoreCase);
    }

    public bool CanApply(string enchantment, ShardItemDefinition item)
    {
        if (!TryGet(enchantment, out var data)) return false;
        string name = item.Identifier.StartsWith("minecraft:", StringComparison.Ordinal)
            ? item.Identifier["minecraft:".Length..] : item.Identifier;
        if (name == "enchanted_book" || name == "book") return true;
        foreach (string kind in data.SupportedItems)
            if (kind switch
            {
                "any" => true,
                "breakable" => item.MaxDurability > 0,
                "armor" => name.EndsWith("_helmet", StringComparison.Ordinal) ||
                    name.EndsWith("_chestplate", StringComparison.Ordinal) ||
                    name.EndsWith("_leggings", StringComparison.Ordinal) ||
                    name.EndsWith("_boots", StringComparison.Ordinal),
                "weapon" => name.EndsWith("_sword", StringComparison.Ordinal) ||
                    name.EndsWith("_axe", StringComparison.Ordinal) ||
                    name.EndsWith("_spear", StringComparison.Ordinal) ||
                    name is "mace" or "trident",
                "tool" => name.EndsWith("_pickaxe", StringComparison.Ordinal) ||
                    name.EndsWith("_axe", StringComparison.Ordinal) ||
                    name.EndsWith("_shovel", StringComparison.Ordinal) ||
                    name.EndsWith("_hoe", StringComparison.Ordinal) ||
                    name is "shears",
                "sword" => name.EndsWith("_sword", StringComparison.Ordinal),
                "helmet" => name.EndsWith("_helmet", StringComparison.Ordinal),
                "boots" => name.EndsWith("_boots", StringComparison.Ordinal),
                "leggings" => name.EndsWith("_leggings", StringComparison.Ordinal),
                "chestplate" => name.EndsWith("_chestplate", StringComparison.Ordinal),
                "bow" => name == "bow",
                "crossbow" => name == "crossbow",
                "trident" => name == "trident",
                "mace" => name == "mace",
                "spear" => name.EndsWith("_spear", StringComparison.Ordinal),
                "fishing_rod" => name == "fishing_rod",
                _ => false
            }) return true;
        return false;
    }

    private static ShardEnchantmentDefinition Make(string id, Fields f,
        ShardEnchantmentDefinition? prior)
    {
        if (f.Id is not null && Normalize(f.Id) != id)
            throw new InvalidDataException($"Enchantment '{id}' cannot change its registry Id.");
        int max = f.MaxLevel ?? prior?.MaxLevel ?? 0;
        int weight = f.Weight ?? prior?.Weight ?? -1;
        int anvil = f.AnvilCost ?? prior?.AnvilCost ?? 0;
        if (max is < 1 or > 255 || weight is < 0 or > 100 || anvil is < 1 or > 100)
            throw new InvalidDataException($"Invalid levels/weights/costs for enchantment '{id}'.");
        string[] supported = f.SupportedItems ?? prior?.SupportedItems.ToArray() ?? [];
        if (supported.Length is < 1 or > 16 || supported.Any(s =>
            s is not ("any" or "breakable" or "armor" or "weapon" or "tool" or
                "sword" or "helmet" or "boots" or "leggings" or "chestplate" or
                "bow" or "crossbow" or "trident" or "mace" or "spear" or "fishing_rod")))
            throw new InvalidDataException($"Invalid SupportedItems in enchantment '{id}'.");
        string[] conflicts = (f.Conflicts ?? prior?.Conflicts.ToArray() ?? [])
            .Select(Normalize).ToArray();
        string[] lines = f.Skills ?? prior?.Skills.Select(s => SkillText(s)).ToArray() ?? [];
        if (lines.Length is < 1 or > 32)
            throw new InvalidDataException($"Each enchantment needs 1..32 Skills ({id}).");
        var skills = lines.Select(line => SkillEngine.Parse(id, line)).ToArray();
        return new ShardEnchantmentDefinition(id, max, weight, anvil,
            Array.AsReadOnly(supported), Array.AsReadOnly(conflicts),
            f.Treasure ?? prior?.Treasure ?? false, f.Curse ?? prior?.Curse ?? false,
            Array.AsReadOnly(skills));
    }

    // Preserve parsed definitions when a metadata-only override is applied,
    // rather than stringifying and re-parsing conditions.
    private static string SkillText(ShardSkillInstruction s)
    {
        static string Args(IReadOnlyDictionary<string, string> a) => a.Count == 0 ? "" :
            "{" + string.Join(';', a.Select(p => p.Key + "=" + p.Value)) + "}";
        return s.Mechanic + Args(s.Arguments) +
            (s.Target is null ? "" : " @" + s.Target) + " ~" + s.Trigger +
            string.Concat(s.Conditions.Select(c => " ?" + c.Name + Args(c.Arguments)));
    }

    private static IEnumerable<(string Id, Fields Fields)> ReadEntries(string path)
    {
        var yaml = new YamlStream();
        try
        {
            using var reader = File.OpenText(path);
            yaml.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"Invalid enchantment YAML {path}: {ex.Message}", ex);
        }
        if (yaml.Documents.Count != 1 ||
            yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException($"Enchantment YAML needs one root mapping ({path}).");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in root.Children)
        {
            string id = Normalize(Scalar(pair.Key, path));
            if (!seen.Add(id) || pair.Value is not YamlMappingNode properties)
                throw new InvalidDataException($"Duplicate or invalid enchantment {id} ({path}).");
            var f = new Fields();
            var propertiesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties.Children)
            {
                string name = Scalar(property.Key, path);
                if (!propertiesSeen.Add(name))
                    throw new InvalidDataException($"Duplicate {name} for {id} ({path}).");
                switch (name.ToLowerInvariant())
                {
                    case "id": f.Id = Scalar(property.Value, path); break;
                    case "maxlevel": f.MaxLevel = Integer(property.Value, path); break;
                    case "weight": f.Weight = Integer(property.Value, path); break;
                    case "anvilcost": f.AnvilCost = Integer(property.Value, path); break;
                    case "supporteditems": f.SupportedItems = Sequence(property.Value, path); break;
                    case "conflicts": f.Conflicts = Sequence(property.Value, path); break;
                    case "skills": f.Skills = Sequence(property.Value, path); break;
                    case "treasure": f.Treasure = Boolean(property.Value, path); break;
                    case "curse": f.Curse = Boolean(property.Value, path); break;
                    default: throw new InvalidDataException(
                        $"Unsupported enchantment field {name} in {path}.");
                }
            }
            yield return (id, f);
        }
    }

    private static string Normalize(string id)
    {
        id = id.Trim().ToLowerInvariant();
        return id.StartsWith("minecraft:", StringComparison.Ordinal) ? id :
            "minecraft:" + id;
    }
    private static string Scalar(YamlNode node, string path) =>
        node is YamlScalarNode { Value: { } value } ? value :
        throw new InvalidDataException($"Expected scalar in {path}.");
    private static int Integer(YamlNode node, string path) =>
        int.TryParse(Scalar(node, path), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int number) ? number :
        throw new InvalidDataException($"Expected integer in {path}.");
    private static bool Boolean(YamlNode node, string path) =>
        bool.TryParse(Scalar(node, path), out bool value) ? value :
        throw new InvalidDataException($"Expected boolean in {path}.");
    private static string[] Sequence(YamlNode node, string path)
    {
        if (node is not YamlSequenceNode sequence || sequence.Children.Count > 32)
            throw new InvalidDataException($"Expected a list of at most 32 items in {path}.");
        return sequence.Children.Select(n => Scalar(n, path)).ToArray();
    }
}
