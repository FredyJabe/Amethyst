using System.Text.Json;

namespace Amethyst.BuiltinShards;

/// <summary>Resolves pinned 26.3 vanilla data-pack tags for loot selectors.
/// Nested #tags are flattened with cycle detection, and required missing
/// tags are errors rather than silent empty lists.</summary>
public sealed class VanillaLootTags
{
    private readonly string _root;
    private readonly Dictionary<string, IReadOnlyList<string>> _cache =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public VanillaLootTags(string directory) =>
        _root = Path.GetFullPath(directory);

    public IReadOnlyList<string> Resolve(string registry, string name)
    {
        if (!name.StartsWith("#", StringComparison.Ordinal))
            throw new InvalidDataException("Loot tag reference must start with #.");
        lock (_gate) return ResolveCore(registry, name[1..],
            new HashSet<string>(StringComparer.Ordinal));
    }

    private IReadOnlyList<string> ResolveCore(string registry, string name,
        HashSet<string> visiting)
    {
        if (!new[] { "item", "enchantment", "entity_type", "instrument" }.Contains(
            registry, StringComparer.Ordinal) ||
            !name.StartsWith("minecraft:", StringComparison.Ordinal))
            throw new NotSupportedException(
                $"Unsupported 26.3 loot tag {registry}/{name}.");
        string key = registry + ":" + name;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        if (visiting.Count > 24 || !visiting.Add(key))
            throw new InvalidDataException($"Recursive loot tag {key}.");
        try
        {
            string path = Path.Combine(_root, registry,
                name["minecraft:".Length..].Replace('/', Path.DirectorySeparatorChar)
                + ".json");
            if (!File.Exists(path))
                throw new NotSupportedException($"Missing vanilla loot tag {key}.");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var result = new List<string>();
            foreach (var raw in json.RootElement.GetProperty("values").EnumerateArray())
            {
                bool required = true;
                string identifier;
                if (raw.ValueKind == JsonValueKind.String)
                    identifier = raw.GetString()!;
                else
                {
                    identifier = raw.GetProperty("id").GetString()!;
                    if (raw.TryGetProperty("required", out var req))
                        required = req.GetBoolean();
                }
                if (identifier.StartsWith("#", StringComparison.Ordinal))
                {
                    try { result.AddRange(ResolveCore(registry, identifier[1..],
                        visiting)); }
                    catch (NotSupportedException) when (!required) { }
                }
                else if (identifier.StartsWith("minecraft:", StringComparison.Ordinal))
                    result.Add(identifier);
                else throw new NotSupportedException(
                    $"Unsupported namespaced tag member {identifier}.");
            }
            var unique = Array.AsReadOnly(result.Distinct(StringComparer.Ordinal)
                .ToArray());
            _cache[key] = unique;
            return unique;
        }
        finally { visiting.Remove(key); }
    }
}
