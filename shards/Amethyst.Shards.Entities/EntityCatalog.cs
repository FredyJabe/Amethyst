using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Amethyst.Shards;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Amethyst.BuiltinShards;

/// <summary>Validated vanilla 26.3 registry plus recursively loaded Mythic-style
/// administrator overrides and aliases. Registry IDs and client hitboxes cannot
/// be changed by configuration.</summary>
public sealed class EntityCatalog : IShardEntityRegistry
{
    private static readonly Regex EntityKey = new(
        @"^([a-z0-9_.-]+:)?[a-z0-9_./-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ShardEntityDefinition[] _byId;
    private readonly IReadOnlyList<ShardEntityDefinition> _all;
    private readonly Dictionary<string, ShardEntityDefinition> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    public string MinecraftVersion => "26.3";
    public int Count => _byId.Length;
    public IReadOnlyList<ShardEntityDefinition> All => _all;

    public EntityCatalog(string vanillaFile, string? overridesDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vanillaFile);
        var vanilla = new List<ShardEntityDefinition>();
        foreach (var (root, fields) in ReadEntries(vanillaFile))
        {
            if (fields.ProtocolId != vanilla.Count || fields.Id is null ||
                fields.Width is null || fields.Height is null || fields.Category is null)
                throw new InvalidDataException(
                    $"Incomplete or out-of-order vanilla entity '{root}' in {vanillaFile}.");
            string name = Normalize(fields.Id);
            if (!name.StartsWith("minecraft:", StringComparison.Ordinal) ||
                name["minecraft:".Length..] != Normalize(root) ||
                !ValidDimensions(fields.Width.Value, fields.Height.Value) ||
                !EntityKey.IsMatch(name))
                throw new InvalidDataException($"Invalid vanilla entity '{root}' in {vanillaFile}.");
            if (string.IsNullOrWhiteSpace(fields.Category) ||
                fields.Category.Length > 64)
                throw new InvalidDataException($"Invalid category for entity '{root}'.");
            var entity = ApplyConfig(new ShardEntityDefinition(vanilla.Count, name,
                Normalize(fields.Category), fields.Width.Value, fields.Height.Value)
            {
                Spawnable = fields.Category != "misc" && name != "minecraft:player"
            }, fields, vanillaFile, root);
            if (_byName.ContainsKey(name))
                throw new InvalidDataException($"Duplicate vanilla entity '{root}'.");
            vanilla.Add(entity);
            _byName.Add(name, entity);
            _byName.Add(name["minecraft:".Length..], entity);
        }
        if (vanilla.Count != 161 || vanilla[72].Identifier != "minecraft:item")
            throw new InvalidDataException("Incorrect Java 26.3 entity registry (expected 161 entries).");
        _byId = vanilla.ToArray();

        if (overridesDirectory is not null)
        {
            Directory.CreateDirectory(overridesDirectory);
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(overridesDirectory, "*",
                SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
                foreach (var (root, fields) in ReadEntries(file))
                    ApplyOverride(root, fields, file, touched);
        }
        _all = new ReadOnlyCollection<ShardEntityDefinition>(_byId);
    }

    public bool TryGetById(int protocolId, out ShardEntityDefinition definition)
    {
        if ((uint)protocolId < (uint)_byId.Length)
        {
            definition = _byId[protocolId];
            return true;
        }
        definition = default;
        return false;
    }

    public bool TryGetByIdentifier(string identifier, out ShardEntityDefinition definition)
    {
        definition = default;
        return !string.IsNullOrWhiteSpace(identifier) &&
            _byName.TryGetValue(Normalize(identifier), out definition);
    }

    private void ApplyOverride(string root, EntityFields fields, string file, HashSet<string> touched)
    {
        string key = Normalize(root);
        if (!EntityKey.IsMatch(key) || !touched.Add(key))
            throw new InvalidDataException($"Invalid or repeated entity ID '{root}' in {file}.");

        bool canonical = _byName.TryGetValue(key, out ShardEntityDefinition original);
        if (!canonical && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture,
            out int numeric))
            canonical = TryGetById(numeric, out original);
        if (canonical)
        {
            if (!touched.Add("protocol:" + original.ProtocolId.ToString(CultureInfo.InvariantCulture)))
                throw new InvalidDataException($"Vanilla entity '{original.Identifier}' overridden more than once.");
            ValidateIdentity(fields, original, root, file);
            var updated = ApplyConfig(original, fields, file, root);
            _byId[original.ProtocolId] = updated;
            _byName[original.Identifier] = updated;
            _byName[original.Identifier["minecraft:".Length..]] = updated;
            return;
        }

        if (key.StartsWith("minecraft:", StringComparison.Ordinal) ||
            int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            fields.Id is null || !TryResolve(fields.Id, out original))
            throw new InvalidDataException(
                $"Custom entity '{root}' in {file} requires Id: <existing vanilla entity>.");
        ValidateIdentity(fields, original, root, file);
        string name = key.Contains(':') ? key : "amethyst:" + key;
        if (_byName.ContainsKey(name))
            throw new InvalidDataException($"Entity alias '{name}' already defined.");
        ShardEntityDefinition alias = ApplyConfig(original with { Identifier = name },
            fields, file, root);
        _byName.Add(name, alias);
        if (key != name) _byName.Add(key, alias);
    }

    private bool TryResolve(string key, out ShardEntityDefinition entity) =>
        int.TryParse(Normalize(key), NumberStyles.None, CultureInfo.InvariantCulture, out int id)
            ? TryGetById(id, out entity)
            : TryGetByIdentifier(key, out entity);

    private static void ValidateIdentity(EntityFields fields, ShardEntityDefinition entity,
        string key, string file)
    {
        // An alias must still use the client's actual geometry and wire ID.
        if (fields.Id is not null &&
            !string.Equals(Normalize(fields.Id),
                entity.Identifier, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Normalize(fields.Id),
                entity.Identifier["minecraft:".Length..], StringComparison.OrdinalIgnoreCase) &&
            fields.Id != entity.ProtocolId.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException($"Entity '{key}' in {file} cannot reassign Id.");
        if (fields.ProtocolId is int id && id != entity.ProtocolId ||
            fields.Category is string category &&
                !string.Equals(category, entity.Category, StringComparison.OrdinalIgnoreCase) ||
            fields.Width is double width && width != entity.Width ||
            fields.Height is double height && height != entity.Height)
            throw new InvalidDataException(
                $"Entity '{key}' in {file} cannot change vanilla ProtocolId, Category or dimensions.");
    }

    private static ShardEntityDefinition ApplyConfig(ShardEntityDefinition entity,
        EntityFields fields, string file, string root)
    {
        double? health = fields.Health ?? entity.MaxHealth;
        if (health is double value && (!double.IsFinite(value) || value <= 0 || value > 2048))
            throw new InvalidDataException($"Invalid Health for entity '{root}' in {file}.");
        int xpMin = fields.ExperienceMin ?? entity.ExperienceMin;
        int xpMax = fields.ExperienceMax ?? entity.ExperienceMax;
        if (xpMin < 0 || xpMax < xpMin || xpMax > short.MaxValue)
            throw new InvalidDataException(
                $"Invalid Experience range for entity '{root}' in {file}.");
        return entity with
        {
            Display = fields.Display ?? entity.Display,
            MaxHealth = health,
            Spawnable = fields.Spawnable ?? entity.Spawnable,
            Invulnerable = fields.Invulnerable ?? entity.Invulnerable,
            ExperienceMin = xpMin,
            ExperienceMax = xpMax
        };
    }

    private static bool ValidDimensions(double width, double height) =>
        double.IsFinite(width) && double.IsFinite(height) &&
        width >= 0 && width <= 128 && height >= 0 && height <= 128;

    private static string Normalize(string text) => text.Trim().ToLowerInvariant();

    private sealed class EntityFields
    {
        public string? Id;
        public int? ProtocolId;
        public string? Category;
        public double? Width;
        public double? Height;
        public double? Health;
        public int? ExperienceMin;
        public int? ExperienceMax;
        public string? Display;
        public bool? Spawnable;
        public bool? Invulnerable;
    }

    private static IEnumerable<(string Key, EntityFields Fields)> ReadEntries(string file)
    {
        var yaml = new YamlStream();
        try
        {
            using var reader = File.OpenText(file);
            yaml.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"Malformed entity YAML '{file}': {ex.Message}", ex);
        }
        if (yaml.Documents.Count == 0) yield break;
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException($"Entities file '{file}' must contain a root mapping.");
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root.Children)
        {
            string key = Scalar(entry.Key, file);
            if (!roots.Add(Normalize(key)) || entry.Value is not YamlMappingNode properties)
                throw new InvalidDataException($"Duplicate or invalid entity root '{key}' in {file}.");
            var fields = new EntityFields();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties.Children)
            {
                string name = Scalar(property.Key, file);
                if (!seen.Add(name)) throw new InvalidDataException($"Duplicate '{name}' in '{key}' ({file}).");
                switch (name.ToLowerInvariant())
                {
                    case "id": fields.Id = Scalar(property.Value, file); break;
                    case "protocolid": fields.ProtocolId = Integer(property.Value, file); break;
                    case "category": fields.Category = Scalar(property.Value, file); break;
                    case "width": fields.Width = Number(property.Value, file); break;
                    case "height": fields.Height = Number(property.Value, file); break;
                    case "display":
                        fields.Display = Scalar(property.Value, file);
                        if (fields.Display.Length > 1024)
                            throw new InvalidDataException($"Display too long in '{key}' ({file}).");
                        break;
                    case "health":
                        if (fields.Health.HasValue)
                            throw new InvalidDataException($"Duplicate Health for '{key}'.");
                        fields.Health = Number(property.Value, file);
                        break;
                    case "experience":
                        throw new InvalidDataException(
                            $"Entity '{key}' no longer supports Experience in Entities YAML. " +
                            "Move experience rewards to Items loot ~onDeath: " +
                            "dropExperience{amount=5} ?flag{name=killed_by_player}.");
                    case "spawnable":
                        if (fields.Spawnable.HasValue)
                            throw new InvalidDataException($"Duplicate Spawnable for '{key}'.");
                        fields.Spawnable = Boolean(property.Value, file);
                        break;
                    case "invulnerable":
                        if (fields.Invulnerable.HasValue)
                            throw new InvalidDataException($"Duplicate Invulnerable for '{key}'.");
                        fields.Invulnerable = Boolean(property.Value, file);
                        break;
                    case "options":
                        if (property.Value is not YamlMappingNode options)
                            throw new InvalidDataException($"Options for '{key}' must be a mapping.");
                        var seenOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var option in options.Children)
                        {
                            string optionName = Scalar(option.Key, file);
                            if (!seenOptions.Add(optionName))
                                throw new InvalidDataException($"Duplicate Options.{optionName} for '{key}' in {file}.");
                            switch (optionName.ToLowerInvariant())
                            {
                                case "health":
                                    if (fields.Health.HasValue) throw new InvalidDataException($"Duplicate Health for '{key}'.");
                                    fields.Health = Number(option.Value, file);
                                    break;
                                case "experience":
                                    throw new InvalidDataException(
                                        $"Entity '{key}' no longer accepts Options.Experience. " +
                                        "Define dropExperience in an Items ~onDeath skill.");
                                case "spawnable":
                                    if (fields.Spawnable.HasValue) throw new InvalidDataException($"Duplicate Spawnable for '{key}'.");
                                    fields.Spawnable = Boolean(option.Value, file);
                                    break;
                                case "invulnerable":
                                    if (fields.Invulnerable.HasValue) throw new InvalidDataException($"Duplicate Invulnerable for '{key}'.");
                                    fields.Invulnerable = Boolean(option.Value, file);
                                    break;
                                default:
                                    throw new InvalidDataException($"Unsupported Options.{optionName} for '{key}' in {file}.");
                            }
                        }
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported entity field '{name}' in '{key}' ({file}).");
                }
            }
            yield return (key, fields);
        }
    }

    /// <summary>XP may be an exact scalar or a two-element inclusive range.</summary>
    private static (int Min, int Max) Experience(YamlNode node, string file)
    {
        if (node is YamlScalarNode)
        {
            int amount = Integer(node, file);
            return (amount, amount);
        }
        if (node is YamlSequenceNode { Children.Count: 2 } sequence)
            return (Integer(sequence.Children[0], file),
                Integer(sequence.Children[1], file));
        throw new InvalidDataException(
            $"Experience must be a number or [min, max] in {file}.");
    }

    private static string Scalar(YamlNode node, string file) =>
        node is YamlScalarNode { Value: { } s } ? s :
        throw new InvalidDataException($"Expected scalar in {file}.");
    private static int Integer(YamlNode node, string file) =>
        int.TryParse(Scalar(node, file), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int value) ? value : throw new InvalidDataException($"Invalid integer in {file}.");
    private static double Number(YamlNode node, string file) =>
        double.TryParse(Scalar(node, file), NumberStyles.Float, CultureInfo.InvariantCulture,
            out double value) && double.IsFinite(value) ? value :
            throw new InvalidDataException($"Invalid number in {file}.");
    private static bool Boolean(YamlNode node, string file) =>
        bool.TryParse(Scalar(node, file), out bool value) ? value :
            throw new InvalidDataException($"Invalid boolean in {file}.");
}
