using System.Globalization;
using System.Text.Json;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>Reads the canonical Java 26.3 data-pack loot JSON produced by
/// Mojang. The upstream dataset is pinned by the git submodule. Unknown
/// conditions, entry types, and modifiers are intentionally rejected:
/// silently ignoring them would mint invalid items.</summary>
public sealed class VanillaLootTables : IShardVanillaLootTables
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly IShardItemRegistry _items;
    private readonly IShardRecipeRegistry? _recipes;
    public int Count => _files.Count;

    public VanillaLootTables(string directory, IShardItemRegistry items,
        IShardRecipeRegistry? recipes = null)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _recipes = recipes;
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(
                $"Vanilla 26.3 loot directory not found: {directory}");
        foreach (string file in Directory.EnumerateFiles(directory, "*.json",
            SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!relative.EndsWith(".json", StringComparison.Ordinal)) continue;
            string id = "minecraft:" + relative[..^5];
            if (!_files.TryAdd(id, file))
                throw new InvalidDataException($"Duplicate loot table {id}.");
        }
        if (_files.Count == 0)
            throw new InvalidDataException("Empty vanilla loot data directory.");
    }

    public bool HasTable(string id) => _files.ContainsKey(id);

    public IReadOnlyList<ShardItemDrop> Roll(string id,
        ShardVanillaLootContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = new List<Drop>();
        Evaluate(id, context, output, new HashSet<string>(StringComparer.Ordinal), 0);
        var drops = new List<ShardItemDrop>();
        foreach (var drop in output)
        {
            if (drop.Count <= 0) continue;
            int maximum = _items.TryGetById(drop.ItemId, out var definition)
                ? definition.MaxStackSize : 64;
            maximum = Math.Clamp(maximum, 1, 64);
            for (int left = drop.Count; left > 0;)
            {
                int amount = Math.Min(left, maximum);
                drops.Add(new ShardItemDrop(drop.ItemId, amount)
                {
                    ComponentData = drop.Components.Count == 0 ? null :
                        JsonSerializer.Serialize(drop.Components)
                });
                left -= amount;
                if (drops.Count > 256)
                    throw new InvalidDataException("Loot table returned excessive items.");
            }
        }
        return drops.AsReadOnly();
    }

    private sealed class Drop(int itemId, int count)
    {
        public int ItemId = itemId;
        public int Count = count;
        public Dictionary<string, string> Components = new(StringComparer.Ordinal);
    }

    private void Evaluate(string id, ShardVanillaLootContext c,
        List<Drop> output, HashSet<string> visiting, int depth)
    {
        if (depth > 16 || !visiting.Add(id))
            throw new InvalidDataException($"Recursive loot table: {id}.");
        try
        {
            if (!_files.TryGetValue(id, out string? path))
                throw new InvalidDataException($"Unknown 26.3 loot table {id}.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("pools", out JsonElement pools))
            {
                foreach (JsonElement pool in pools.EnumerateArray())
                {
                    if (!Pass(pool, c)) continue;
                    double bonus = pool.TryGetProperty("bonus_rolls", out var extra)
                        ? Sample(extra, c) * c.Luck : 0;
                    double count = Sample(pool.GetProperty("rolls"), c) + bonus;
                    int rolls = (int)Math.Clamp(Math.Floor(count) +
                        (c.Random.NextDouble() < count - Math.Floor(count) ? 1 : 0),
                        0, 256);
                    for (int roll = 0; roll < rolls; roll++)
                    {
                        var entries = pool.GetProperty("entries").EnumerateArray()
                            .Where(e => Pass(e, c)).ToArray();
                        int total = 0;
                        foreach (JsonElement entry in entries)
                            total = checked(total + EffectiveWeight(entry, c));
                        if (total == 0) continue;
                        int selected = c.Random.Next(total);
                        foreach (JsonElement entry in entries)
                        {
                            int weight = EffectiveWeight(entry, c);
                            if (selected >= weight) { selected -= weight; continue; }
                            var generated = new List<Drop>();
                            Emit(entry, c, generated, visiting, depth);
                            ApplyModifiers(pool, c, generated);
                            output.AddRange(generated);
                            break;
                        }
                    }
                }
            }
            ApplyModifiers(root, c, output);
        }
        finally { visiting.Remove(id); }
    }

    private static int EffectiveWeight(JsonElement entry, ShardVanillaLootContext c)
    {
        int weight = entry.TryGetProperty("weight", out var w) ? w.GetInt32() : 1;
        int quality = entry.TryGetProperty("quality", out var q) ? q.GetInt32() : 0;
        return (int)Math.Clamp((long)weight +
            Math.Floor(quality * c.Luck), 0, 100000);
    }

    private bool Emit(JsonElement entry, ShardVanillaLootContext c,
        List<Drop> output, HashSet<string> visiting, int depth)
    {
        if (!Pass(entry, c)) return false;
        string type = Type(entry);
        var generated = new List<Drop>();
        switch (type)
        {
            case "minecraft:empty": break;
            case "minecraft:item":
            {
                string name = entry.GetProperty("name").GetString() ??
                    throw new InvalidDataException("Missing loot item name.");
                if (!_items.TryGetByIdentifier(name, out var definition) ||
                    definition.ProtocolId == 0)
                    throw new InvalidDataException($"Unregistered loot material {name}.");
                generated.Add(new Drop(definition.ProtocolId, 1));
                break;
            }
            case "minecraft:loot_table":
            {
                var value = entry.GetProperty("value");
                if (value.ValueKind != JsonValueKind.String)
                    throw new NotSupportedException("26.3 inline/tag loot_table entries need context expansion.");
                Evaluate(value.GetString()!, c, generated, visiting, depth + 1);
                break;
            }
            case "minecraft:alternatives":
                foreach (JsonElement child in entry.GetProperty("children").EnumerateArray())
                    if (Emit(child, c, generated, visiting, depth + 1)) break;
                break;
            case "minecraft:group":
                foreach (JsonElement child in entry.GetProperty("children").EnumerateArray())
                    Emit(child, c, generated, visiting, depth + 1);
                break;
            case "minecraft:sequence":
                foreach (JsonElement child in entry.GetProperty("children").EnumerateArray())
                    if (!Emit(child, c, generated, visiting, depth + 1)) break;
                break;
            default:
                throw new NotSupportedException($"Unsupported 26.3 loot entry {type}.");
        }
        ApplyModifiers(entry, c, generated);
        output.AddRange(generated);
        return true;
    }

    private static string Type(JsonElement e) =>
        e.GetProperty("type").GetString() ??
            throw new InvalidDataException("Missing loot type.");

    private static bool Pass(JsonElement parent, ShardVanillaLootContext c) =>
        !parent.TryGetProperty("condition", out var condition) ||
        Condition(condition, c);

    private static bool Condition(JsonElement cond, ShardVanillaLootContext c)
    {
        string type = cond.ValueKind == JsonValueKind.String ?
            cond.GetString()! : Type(cond);
        switch (type)
        {
            case "minecraft:tool/can_silk_touch": return c.SilkTouch;
            case "minecraft:tool/can_shear": return c.Shears;
            case "minecraft:survives_explosion":
                return c.ExplosionRadius is not double radius ||
                    c.Random.NextDouble() < Math.Min(1, 1 / Math.Max(radius, 0.0001));
            case "minecraft:inverted":
                return !Condition(cond.GetProperty("term"), c);
            case "minecraft:any_of":
                return cond.GetProperty("terms").EnumerateArray().Any(t => Condition(t, c));
            case "minecraft:all_of":
                return cond.GetProperty("terms").EnumerateArray().All(t => Condition(t, c));
            case "minecraft:random_chance":
                return c.Random.NextDouble() <
                    Sample(cond.GetProperty("chance"), c);
            case "minecraft:table_bonus":
            {
                var chances = cond.GetProperty("chances").EnumerateArray().ToArray();
                string enchant = cond.GetProperty("enchantment").GetString() ?? "";
                int level = enchant.EndsWith(":fortune", StringComparison.Ordinal)
                    ? c.Fortune : c.Looting;
                return chances.Length > 0 && c.Random.NextDouble() <
                    chances[Math.Clamp(level, 0, chances.Length - 1)].GetDouble();
            }
            case "minecraft:killed_by_player": return c.KilledByPlayer;
            case "minecraft:entity_properties":
                return EntityProperties(cond, c);
            case "minecraft:block_state_property":
            {
                string block = cond.GetProperty("block").GetString() ?? "";
                if (!string.Equals(block, c.BlockId, StringComparison.Ordinal))
                    return false;
                if (!cond.TryGetProperty("properties", out var properties)) return true;
                foreach (JsonProperty prop in properties.EnumerateObject())
                {
                    if (c.BlockProperties is null ||
                        !c.BlockProperties.TryGetValue(prop.Name, out string? actual))
                        throw new NotSupportedException(
                            $"Block loot needs property '{prop.Name}' in the context.");
                    if (!string.Equals(actual, prop.Value.ToString(), StringComparison.Ordinal))
                        return false;
                }
                return true;
            }
            case "minecraft:location_check":
                if (cond.TryGetProperty("predicate", out var predicate) &&
                    predicate.TryGetProperty("biomes", out var biomes))
                {
                    if (c.Biome is null)
                        throw new NotSupportedException("Loot location requires a biome.");
                    return biomes.ValueKind == JsonValueKind.String
                        ? biomes.GetString() == c.Biome
                        : biomes.EnumerateArray().Any(e => e.GetString() == c.Biome);
                }
                throw new NotSupportedException("Unsupported 26.3 location_check predicate.");
            default:
                throw new NotSupportedException($"Unsupported 26.3 loot condition {type}.");
        }
    }

    private static bool EntityProperties(JsonElement cond, ShardVanillaLootContext c)
    {
        string who = cond.GetProperty("entity").GetString() ?? "";
        if (who == "direct_attacker" && cond.TryGetProperty("predicate", out var attacker) &&
            attacker.TryGetProperty("minecraft:equipment", out _))
            return c.FireAspect;
        if (who != "this")
            throw new NotSupportedException($"Loot entity predicate for {who} needs a full entity context.");
        if (!cond.TryGetProperty("predicate", out var pred)) return true;
        foreach (JsonProperty part in pred.EnumerateObject())
        {
            if (part.Name == "minecraft:flags" &&
                part.Value.TryGetProperty("is_on_fire", out var fire))
            {
                if (c.Burning != fire.GetBoolean()) return false;
            }
            else if (part.Name == "minecraft:components" &&
                part.Value.TryGetProperty("minecraft:sheep/color", out var color))
            {
                if (color.GetString() != c.EntityColor) return false;
            }
            else if (part.Name == "minecraft:type_specific/sheep" &&
                part.Value.TryGetProperty("sheared", out var sheared))
            {
                if (c.EntitySheared != sheared.GetBoolean()) return false;
            }
            else if (part.Name == "minecraft:type_specific/fishing_hook" &&
                part.Value.TryGetProperty("in_open_water", out var open))
            {
                if (c.InOpenWater != open.GetBoolean()) return false;
            }
            else
                throw new NotSupportedException(
                    $"Unsupported entity loot predicate {part.Name}.");
        }
        return true;
    }

    private void ApplyModifiers(JsonElement parent,
        ShardVanillaLootContext c, List<Drop> drops)
    {
        if (!parent.TryGetProperty("modifier", out var modifier)) return;
        if (modifier.ValueKind == JsonValueKind.Array)
            foreach (var item in modifier.EnumerateArray())
                ApplyModifier(item, c, drops);
        else ApplyModifier(modifier, c, drops);
    }

    private void ApplyModifier(JsonElement modifier,
        ShardVanillaLootContext c, List<Drop> drops)
    {
        if (!Pass(modifier, c)) return;
        string type = Type(modifier);
        switch (type)
        {
            case "minecraft:set_count":
            {
                int amount = (int)Math.Clamp(Math.Floor(
                    Sample(modifier.GetProperty("count"), c)), 0, 16384);
                bool add = modifier.TryGetProperty("add", out var flag) &&
                    flag.GetBoolean();
                foreach (var drop in drops)
                    drop.Count = add ? checked(drop.Count + amount) : amount;
                break;
            }
            case "minecraft:enchanted_count_increase":
            {
                string id = modifier.GetProperty("enchantment").GetString() ?? "";
                int level = id == "minecraft:looting" ? c.Looting :
                    id == "minecraft:fortune" ? c.Fortune : 0;
                foreach (var drop in drops)
                    drop.Count += (int)Math.Floor(level *
                        Sample(modifier.GetProperty("count"), c) + 0.5);
                break;
            }
            case "minecraft:apply_bonus":
            {
                string formula = modifier.GetProperty("formula").GetString() ?? "";
                int level = c.Fortune;
                foreach (var drop in drops)
                {
                    switch (formula)
                    {
                        case "minecraft:ore_drops":
                            drop.Count *= level <= 0 ? 1 :
                                Math.Max(1, c.Random.Next(level + 2));
                            break;
                        case "minecraft:uniform_bonus_count":
                        {
                            int multiplier = modifier.TryGetProperty("parameters",
                                out var parameters) &&
                                parameters.TryGetProperty("bonusMultiplier", out var factor)
                                ? factor.GetInt32() : 1;
                            drop.Count += c.Random.Next(level * multiplier + 1);
                            break;
                        }
                        default:
                            throw new NotSupportedException(
                                $"Unsupported Fortune loot formula {formula}.");
                    }
                }
                break;
            }
            case "minecraft:explosion_decay":
                if (c.ExplosionRadius is double radius && radius > 0)
                    foreach (var drop in drops)
                    {
                        int remaining = 0;
                        for (int i = 0; i < drop.Count; i++)
                            if (c.Random.NextDouble() < Math.Min(1, 1 / radius))
                                remaining++;
                        drop.Count = remaining;
                    }
                break;
            case "minecraft:limit_count":
            {
                JsonElement limit = modifier.GetProperty("limit");
                int lower = limit.TryGetProperty("min", out var low) ?
                    low.GetInt32() : 0;
                int upper = limit.TryGetProperty("max", out var high) ?
                    high.GetInt32() : 16384;
                foreach (var drop in drops)
                    drop.Count = Math.Clamp(drop.Count, lower, upper);
                break;
            }
            case "minecraft:furnace_smelt":
                if (c.Burning || c.FireAspect)
                    foreach (var drop in drops)
                    {
                        if (_recipes is null) continue;
                        if (_recipes.TryGetCookingRecipe(ShardCookingStation.Furnace,
                            drop.ItemId, out var recipe))
                            drop.ItemId = recipe.Result.ItemId;
                    }
                break;
            case "minecraft:set_damage":
                foreach (var drop in drops)
                {
                    if (!_items.TryGetById(drop.ItemId, out var item) ||
                        item.MaxDurability <= 0) continue;
                    double remaining = Math.Clamp(
                        Sample(modifier.GetProperty("damage"), c), 0, 1);
                    int damage = (int)Math.Floor(item.MaxDurability *
                        (1 - remaining));
                    drop.Components["minecraft:damage"] =
                        JsonSerializer.Serialize(damage);
                }
                break;
            case "minecraft:set_potion":
            {
                string potion = modifier.GetProperty("id").GetString() ?? "";
                foreach (var drop in drops)
                    drop.Components["minecraft:potion_contents"] =
                        JsonSerializer.Serialize(new { potion });
                break;
            }
            default:
                throw new NotSupportedException(
                    $"Unsupported 26.3 loot modifier {type}.");
        }
    }

    private static double Sample(JsonElement provider,
        ShardVanillaLootContext context)
    {
        if (provider.ValueKind == JsonValueKind.Number) return provider.GetDouble();
        string type = Type(provider);
        return type switch
        {
            "minecraft:constant" =>
                provider.GetProperty("value").GetDouble(),
            "minecraft:uniform" => Uniform(provider, context),
            "minecraft:binomial" => Binomial(provider, context),
            _ => throw new NotSupportedException(
                $"Unsupported 26.3 loot number provider {type}.")
        };
    }

    private static double Uniform(JsonElement provider,
        ShardVanillaLootContext c)
    {
        double min = Sample(provider.GetProperty("min"), c);
        double max = Sample(provider.GetProperty("max"), c);
        if (min > max) throw new InvalidDataException(
            "Invalid uniform loot number provider range.");
        if (Math.Floor(min) == min && Math.Floor(max) == max &&
            min >= 0 && max < int.MaxValue)
            return c.Random.Next((int)min, (int)max + 1);
        return min + c.Random.NextDouble() * (max - min);
    }

    private static double Binomial(JsonElement provider,
        ShardVanillaLootContext c)
    {
        int trials = (int)Math.Clamp(Sample(provider.GetProperty("n"), c), 0, 1024);
        double p = Math.Clamp(Sample(provider.GetProperty("p"), c), 0, 1);
        int hits = 0;
        for (int i = 0; i < trials; i++)
            if (c.Random.NextDouble() < p) hits++;
        return hits;
    }
}
