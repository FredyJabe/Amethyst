using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Amethyst.Shards;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Amethyst.BuiltinShards;

/// <summary>Entity skills owned by the Items Shard. Supports Mythic-style
/// dropItem{item=...;amount=...;chance=...} @self ~onDeath syntax.</summary>
public sealed class SkillCatalog : IShardSkillRegistry
{
    private static readonly Regex EntityIdPattern = new(
        @"^([a-z0-9_.-]+:)?[a-z0-9_./-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Mechanic = new(
        @"^\s*dropItem\s*\{(?<args>[^{}]{1,512})\}\s*(?:@self\s*)?~onDeath\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AmountPattern = new(
        @"^(?<min>\d{1,3})(?:\s*-\s*(?<max>\d{1,3}))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<string, IReadOnlyList<ShardSkillDefinition>> _owners;
    private readonly IReadOnlyDictionary<int, int> _stackSizes;
    public int OwnerCount => _owners.Count;
    public IReadOnlyList<ShardSkillDefinition> Definitions { get; }

    public SkillCatalog(IShardItemRegistry items, string defaultsFile,
        string? overridesDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultsFile);
        var owners = new Dictionary<string, IReadOnlyList<ShardSkillDefinition>>(
            StringComparer.OrdinalIgnoreCase);
        var sizes = new Dictionary<int, int>();
        foreach (var (id, skills) in ReadFile(defaultsFile, items, sizes))
            if (!owners.TryAdd(id, skills))
                throw new InvalidDataException($"Repeated bundled skill owner '{id}'.");

        if (overridesDirectory is not null)
        {
            Directory.CreateDirectory(overridesDirectory);
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(overridesDirectory, "*",
                SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
                foreach (var (id, skills) in ReadFile(file, items, sizes))
                {
                    if (!touched.Add(id))
                        throw new InvalidDataException(
                            $"Duplicate custom skill owner '{id}' ({file}).");
                    owners[id] = skills;
                }
        }
        _owners = new ReadOnlyDictionary<string, IReadOnlyList<ShardSkillDefinition>>(owners);
        _stackSizes = new ReadOnlyDictionary<int, int>(sizes);
        Definitions = Array.AsReadOnly(owners.Values.SelectMany(x => x)
            .OrderBy(x => x.OwnerId, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public bool TryGetSkills(string entityId, out IReadOnlyList<ShardSkillDefinition> skills)
    {
        skills = Array.Empty<ShardSkillDefinition>();
        if (string.IsNullOrWhiteSpace(entityId)) return false;
        string key = Normalize(entityId);
        if (_owners.TryGetValue(key, out var found))
        {
            skills = found;
            return true;
        }
        // The Entities Shard assigns amethyst: to unqualified custom aliases.
        if (key.StartsWith("amethyst:", StringComparison.Ordinal) &&
            _owners.TryGetValue(key["amethyst:".Length..], out found))
        {
            skills = found;
            return true;
        }
        return false;
    }

    public bool TryExecute(string entityId, ShardSkillTrigger trigger,
        out ShardSkillDrop[] drops, Random? random = null)
    {
        drops = [];
        if (!TryGetSkills(entityId, out var skills)) return false;
        random ??= Random.Shared;
        var rolled = new List<ShardSkillDrop>();
        foreach (var skill in skills)
        {
            if (skill.Trigger != trigger) continue;
            if (skill.Action != ShardSkillAction.DropItem)
                throw new InvalidOperationException($"Unsupported skill action {skill.Action}.");
            if (random.NextDouble() >= skill.Chance) continue;
            int amount = random.Next(skill.Min, skill.Max + 1);
            int stackLimit = _stackSizes[skill.ItemId];
            while (amount > 0)
            {
                int count = Math.Min(amount, stackLimit);
                rolled.Add(new ShardSkillDrop(skill.ItemId, count));
                amount -= count;
            }
        }
        drops = rolled.ToArray();
        return true;
    }

    private static IEnumerable<(string Id, IReadOnlyList<ShardSkillDefinition> Skills)>
        ReadFile(string path, IShardItemRegistry items, Dictionary<int, int> sizes)
    {
        var yaml = new YamlStream();
        try
        {
            using var reader = File.OpenText(path);
            yaml.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"Malformed skills YAML '{path}': {ex.Message}", ex);
        }
        if (yaml.Documents.Count == 0) yield break;
        if (yaml.Documents.Count != 1 ||
            yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException($"Skills YAML must have a root mapping ({path}).");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root.Children)
        {
            string id = Normalize(Scalar(entry.Key, path));
            if (!EntityIdPattern.IsMatch(id) || !seen.Add(id))
                throw new InvalidDataException($"Duplicate or invalid entity ID '{id}' ({path}).");
            if (entry.Value is not YamlMappingNode properties)
                throw new InvalidDataException($"Skills for '{id}' must be a mapping ({path}).");
            bool enabled = true;
            YamlSequenceNode? nodeList = null;
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in properties.Children)
            {
                string key = Scalar(field.Key, path);
                if (!keys.Add(key))
                    throw new InvalidDataException($"Repeated field '{key}' in '{id}' ({path}).");
                switch (key.ToLowerInvariant())
                {
                    case "enabled": enabled = Boolean(field.Value, path); break;
                    case "skills":
                        nodeList = field.Value as YamlSequenceNode ??
                            throw new InvalidDataException(
                                $"Skills must be a YAML list for '{id}' ({path}).");
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Unsupported skills property '{key}' in '{id}' ({path}).");
                }
            }
            if (enabled && nodeList is null)
                throw new InvalidDataException($"Entity '{id}' needs Skills (or Enabled: false).");
            if (nodeList is { Children.Count: > 32 })
                throw new InvalidDataException($"Too many skills for '{id}' ({path}).");
            var skills = new List<ShardSkillDefinition>();
            if (enabled && nodeList is not null)
                foreach (YamlNode node in nodeList.Children)
                    skills.Add(Parse(Scalar(node, path), id, path, items, sizes));
            yield return (id, Array.AsReadOnly(skills.ToArray()));
        }
    }

    private static ShardSkillDefinition Parse(string expression, string owner,
        string path, IShardItemRegistry items, Dictionary<int, int> sizes)
    {
        if (expression.Length > 1024)
            throw new InvalidDataException($"Skill for '{owner}' exceeds 1024 characters ({path}).");
        Match match = Mechanic.Match(expression);
        if (!match.Success)
            throw new InvalidDataException(
                $"Unsupported skill '{expression}' for '{owner}' ({path}). " +
                "Expected dropItem{item=...;amount=1-3;chance=1} ~onDeath.");
        string? itemName = null;
        string amount = "1";
        double chance = 1;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string parameter in match.Groups["args"].Value.Split(';'))
        {
            int equals = parameter.IndexOf('=');
            if (equals <= 0 || equals == parameter.Length - 1)
                throw new InvalidDataException(
                    $"Invalid parameter '{parameter}' for '{owner}' ({path}).");
            string rawKey = parameter[..equals].Trim().ToLowerInvariant();
            string value = parameter[(equals + 1)..].Trim();
            string key = rawKey switch
            {
                "i" => "item",
                "a" => "amount",
                "c" => "chance",
                _ => rawKey
            };
            if (!seen.Add(key))
                throw new InvalidDataException(
                    $"Repeated dropItem parameter '{key}' for '{owner}' ({path}).");
            switch (key)
            {
                case "item": itemName = value; break;
                case "amount": amount = value; break;
                case "chance":
                    if (!double.TryParse(value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out chance) ||
                        !double.IsFinite(chance) || chance < 0 || chance > 1)
                        throw new InvalidDataException(
                            $"Chance must be between 0 and 1 for '{owner}' ({path}).");
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported dropItem parameter '{key}' for '{owner}' ({path}).");
            }
        }
        if (itemName is null || !items.TryGetByIdentifier(itemName, out var item) ||
            item.ProtocolId <= 0 || item.MaxStackSize <= 0)
            throw new InvalidDataException(
                $"Unknown or invalid item '{itemName}' in '{owner}' ({path}).");
        Match range = AmountPattern.Match(amount);
        if (!range.Success ||
            !int.TryParse(range.Groups["min"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out int min) ||
            !int.TryParse(range.Groups["max"].Success
                    ? range.Groups["max"].Value : range.Groups["min"].Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out int max) ||
            min < 0 || max < min || max > 64)
            throw new InvalidDataException(
                $"Invalid amount '{amount}' in '{owner}' ({path}); max 64.");
        sizes[item.ProtocolId] = item.MaxStackSize;
        return new ShardSkillDefinition(owner, ShardSkillAction.DropItem,
            ShardSkillTrigger.OnDeath, item.ProtocolId, min, max, chance);
    }

    private static string Normalize(string key)
    {
        string id = key.Trim().ToLowerInvariant();
        return id.StartsWith("minecraft:", StringComparison.Ordinal)
            ? id["minecraft:".Length..] : id;
    }
    private static string Scalar(YamlNode node, string file) =>
        node is YamlScalarNode { Value: { } text } ? text :
            throw new InvalidDataException($"Expected scalar in skills YAML '{file}'.");
    private static bool Boolean(YamlNode node, string file) =>
        bool.TryParse(Scalar(node, file), out bool result) ? result :
            throw new InvalidDataException($"Invalid boolean in skills YAML '{file}'.");
}
