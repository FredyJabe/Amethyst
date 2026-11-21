using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Amethyst.Shards;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Amethyst.BuiltinShards;

/// <summary>Declarative loot definitions owned by Items. Each root is a
/// minecraft:entity identifier (or minecraft:fishing) with an ordered
/// Skills sequence. User
/// definitions replace bundled packs by owner ID, including Skills: [].</summary>
public sealed class LootCatalog : IShardLootRegistry
{
    private static readonly Regex EntityName = new(
        @"^[a-z0-9_.-]+:[a-z0-9_./-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ShardSkillInstruction>> _entries;
    public int Count => _entries.Count;
    public IReadOnlyList<ShardSkillInstruction> Instructions { get; }
    public bool HasDefinition(string ownerId) => _entries.ContainsKey(ownerId);
    private readonly HashSet<string> _overridden =
        new(StringComparer.OrdinalIgnoreCase);
    public bool HasOverride(string ownerId) => _overridden.Contains(ownerId);

    public LootCatalog(string bundledFile, string? overridesDirectory = null)
    {
        var entries = new Dictionary<string, IReadOnlyList<ShardSkillInstruction>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (owner, instructions) in ReadFile(bundledFile))
        {
            if (!entries.TryAdd(owner, instructions))
                throw new InvalidDataException($"Duplicate loot owner {owner} in {bundledFile}.");
        }
        if (overridesDirectory is not null)
        {
            Directory.CreateDirectory(overridesDirectory);
            var overridden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(overridesDirectory, "*",
                SearchOption.AllDirectories)
                .Where(file => file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
            foreach (var (owner, instructions) in ReadFile(file))
            {
                if (!overridden.Add(owner))
                    throw new InvalidDataException(
                        $"Loot owner {owner} overridden more than once.");
                entries[owner] = instructions;
                _overridden.Add(owner);
            }
        }
        _entries = new ReadOnlyDictionary<string, IReadOnlyList<ShardSkillInstruction>>(entries);
        Instructions = Array.AsReadOnly(entries.Values
            .SelectMany(x => x).ToArray());
    }

    private static IEnumerable<(string Owner, IReadOnlyList<ShardSkillInstruction> Skills)>
        ReadFile(string file)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = File.OpenText(file);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"Invalid loot YAML {file}: {ex.Message}", ex);
        }
        if (stream.Documents.Count != 1 ||
            stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException($"Loot YAML requires one mapping: {file}.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root.Children)
        {
            if (entry.Key is not YamlScalarNode { Value: { } owner } ||
                !EntityName.IsMatch(owner) || !seen.Add(owner) ||
                entry.Value is not YamlMappingNode data || data.Children.Count != 1)
                throw new InvalidDataException($"Invalid or repeated loot owner in {file}.");
            var option = data.Children.Single();
            if (option.Key is not YamlScalarNode { Value: "Skills" } ||
                option.Value is not YamlSequenceNode list || list.Children.Count > 32)
                throw new InvalidDataException(
                    $"Loot {owner} needs Skills: [..] with at most 32 entries.");
            var skills = new List<ShardSkillInstruction>();
            foreach (var line in list.Children)
            {
                if (line is not YamlScalarNode { Value: { } expression })
                    throw new InvalidDataException($"Invalid skill in loot {owner}.");
                ShardSkillInstruction skill = SkillEngine.Parse(owner, expression, file);
                string expected = owner == "minecraft:fishing" ?
                    "onFishing" : "onDeath";
                if (skill.Trigger != expected)
                    throw new InvalidDataException(
                        $"Loot {owner} supports only ~{expected}; got ~{skill.Trigger}.");
                skills.Add(skill);
            }
            yield return (owner, Array.AsReadOnly(skills.ToArray()));
        }
    }
}
