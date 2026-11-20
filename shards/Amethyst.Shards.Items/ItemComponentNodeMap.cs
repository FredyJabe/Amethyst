using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Amethyst.Shards;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Amethyst.BuiltinShards;

/// <summary>Maps all 122 Java 26.3 item data components to editable
/// administrator YAML node names. A component may be addressed either by
/// its full namespaced registry ID or the user-selected node name. Aliases
/// affect parsing only; the wire always uses pinned component registry IDs.</summary>
public sealed class ItemComponentNodeMap
{
    private static readonly Regex NodeName = new(
        @"^[A-Za-z_][A-Za-z0-9_.-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<string, string> _nodes;
    private readonly IReadOnlyDictionary<string, string> _aliases;

    public IReadOnlyDictionary<string, string> Nodes => _nodes;

    public ItemComponentNodeMap(string? yamlPath = null)
    {
        var byComponent = ShardItemComponentIds.All.ToDictionary(
            identifier => identifier, identifier => DefaultNode(identifier),
            StringComparer.Ordinal);
        if (yamlPath is not null && File.Exists(yamlPath))
        {
            var yaml = new YamlStream();
            try
            {
                using var reader = File.OpenText(yamlPath);
                yaml.Load(reader);
            }
            catch (YamlException ex)
            {
                throw new InvalidDataException(
                    $"Invalid component nodes YAML '{yamlPath}': {ex.Message}", ex);
            }
            if (yaml.Documents.Count != 1 ||
                yaml.Documents[0].RootNode is not YamlMappingNode mapping)
                throw new InvalidDataException(
                    $"Component nodes YAML '{yamlPath}' must be a root mapping.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in mapping.Children)
            {
                string component = ReadScalar(pair.Key, yamlPath).Trim().ToLowerInvariant();
                if (!component.Contains(':')) component = "minecraft:" + component;
                if (!ShardItemComponentIds.TryGetId(component, out _) ||
                    !seen.Add(component))
                    throw new InvalidDataException(
                        $"Unknown or duplicate component '{component}' in {yamlPath}.");
                string node = ReadScalar(pair.Value, yamlPath).Trim();
                if (!NodeName.IsMatch(node))
                    throw new InvalidDataException(
                        $"Invalid node '{node}' for {component} in {yamlPath}.");
                byComponent[component] = node;
            }
        }

        var byAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (component, node) in byComponent)
        {
            if (!NodeName.IsMatch(node))
                throw new InvalidDataException(
                    $"Invalid YAML node name '{node}' for {component}.");
            if (!byAlias.TryAdd(node, component))
                throw new InvalidDataException(
                    $"YAML node '{node}' is assigned to more than one component.");
            // Canonical IDs must win over administrator aliases. Do not allow
            // an alias like custom_data to shadow a different component ID.
            if (ShardItemComponentIds.TryGetId(node, out int resolved) &&
                ShardItemComponentIds.TryGetId(component, out int actual) &&
                resolved != actual)
                throw new InvalidDataException(
                    $"YAML node '{node}' would shadow a different component.");
        }
        _nodes = new ReadOnlyDictionary<string, string>(byComponent);
        _aliases = new ReadOnlyDictionary<string, string>(byAlias);
    }

    public bool TryResolve(string yamlNode, out string component)
    {
        component = string.Empty;
        if (string.IsNullOrWhiteSpace(yamlNode)) return false;
        string normalized = yamlNode.Trim();
        if (ShardItemComponentIds.TryGetId(normalized.ToLowerInvariant(), out int id))
            return ShardItemComponentIds.TryGetIdentifier(id, out component);
        return _aliases.TryGetValue(normalized, out component!);
    }

    /// <summary>Creates the first-startup template, one editable field for
    /// each registry type, and never overwrites an administrator's changes.</summary>
    public static void WriteTemplateIfMissing(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var text = new StringBuilder();
        text.AppendLine("# Minecraft Java 26.3 item components: component ID -> YAML node name.");
        text.AppendLine("# Edit the VALUES to rename nodes under Components: or Options:.");
        text.AppendLine("# Components may always use their minecraft: registry IDs.");
        text.AppendLine("# Complex component values use explicit WireHex until a typed codec exists.");
        text.AppendLine("# Never reorder or rename the minecraft: KEYS.");
        foreach (string component in ShardItemComponentIds.All)
            text.AppendLine($"{component}: {DefaultNode(component)}");
        File.WriteAllText(path, text.ToString());
    }

    public static string DefaultNode(string identifier)
    {
        string raw = identifier[(identifier.IndexOf(':') + 1)..];
        var words = Regex.Split(raw, "[_/.-]");
        return string.Concat(words.Select(word =>
            word.Length == 0 ? "" : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string ReadScalar(YamlNode node, string path) =>
        node is YamlScalarNode { Value: { } text } ? text :
        throw new InvalidDataException(
            $"Component nodes must be scalar values in '{path}'.");
}
