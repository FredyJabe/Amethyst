using System.Text.Json;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

public sealed partial class VanillaLootTables
{
    /// <summary>Applies the canonical filtered loot function to individual
    /// stacks. Discard removes only the stack that failed its own predicate,
    /// not unrelated rolls from the same pool.</summary>
    private void ApplyFiltered(JsonElement modifier, ShardVanillaLootContext c,
        List<Drop> drops)
    {
        var filter = modifier.GetProperty("item_filter");
        var result = new List<Drop>();
        foreach (Drop drop in drops)
        {
            bool matches = MatchesItemFilter(filter, drop);
            string branch = matches ? "on_pass" : "on_fail";
            var one = new List<Drop> { drop };
            if (modifier.TryGetProperty(branch, out var action))
                ApplyModifier(action, c, one);
            result.AddRange(one);
        }
        drops.Clear();
        drops.AddRange(result);
    }

    private bool MatchesItemFilter(JsonElement filter, Drop drop)
    {
        if (filter.ValueKind != JsonValueKind.Object)
            throw new NotSupportedException("Loot item_filter must be a predicate object.");
        if (!_items.TryGetById(drop.ItemId, out var material))
            throw new InvalidDataException("Loot filter referenced an unknown item.");
        foreach (JsonProperty part in filter.EnumerateObject())
        {
            switch (part.Name)
            {
                case "items":
                {
                    bool found = false;
                    foreach (string selector in ReadSelectors(part.Value))
                        found |= selector.StartsWith("#", StringComparison.Ordinal)
                            ? ReadTag("item", selector).Contains(
                                material.Identifier, StringComparer.Ordinal)
                            : selector == material.Identifier;
                    if (!found) return false;
                    break;
                }
                case "predicates":
                    foreach (JsonProperty predicate in part.Value.EnumerateObject())
                    {
                        if (predicate.Name != "minecraft:map_id" ||
                            predicate.Value.ValueKind != JsonValueKind.Object ||
                            predicate.Value.EnumerateObject().Any())
                            throw new NotSupportedException(
                                $"Unsupported item-filter predicate {predicate.Name}.");
                        // An empty map_id predicate checks for a map component;
                        // it must not match an uninitialized empty map.
                        if (!drop.Components.ContainsKey("minecraft:map_id") &&
                            (material.Components is null ||
                             !material.Components.ContainsKey("minecraft:map_id")))
                            return false;
                    }
                    break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported 26.3 loot item_filter field {part.Name}.");
            }
        }
        return true;
    }

    private bool DamageSourceProperties(JsonElement condition,
        ShardVanillaLootContext context)
    {
        JsonElement predicate = condition.GetProperty("predicate");
        if (predicate.ValueKind != JsonValueKind.Object)
            throw new NotSupportedException("Invalid damage source predicate.");
        foreach (JsonProperty entry in predicate.EnumerateObject())
        {
            switch (entry.Name)
            {
                case "tags":
                    if (context.DamageTypeIdentifier is null)
                        throw new NotSupportedException(
                            "Native loot damage source requires an authoritative damage type.");
                    foreach (JsonElement tag in entry.Value.EnumerateArray())
                    {
                        string id = tag.GetProperty("id").GetString()!;
                        bool expected = tag.TryGetProperty("expected", out var flag)
                            ? flag.GetBoolean() : true;
                        bool actual = id.StartsWith("#", StringComparison.Ordinal)
                            ? ReadTag("damage_type", id).Contains(
                                context.DamageTypeIdentifier, StringComparer.Ordinal)
                            : id == context.DamageTypeIdentifier;
                        if (actual != expected) return false;
                    }
                    break;
                case "direct_entity":
                case "source_entity":
                {
                    string? observed = entry.Name == "direct_entity"
                        ? context.DirectDamagerKind : context.AttackerKind;
                    if (observed is null)
                        throw new NotSupportedException(
                            $"Native loot damage source needs {entry.Name} identity.");
                    JsonElement selector = entry.Value;
                    if (selector.ValueKind != JsonValueKind.Object ||
                        selector.EnumerateObject().Count() != 1 ||
                        !selector.TryGetProperty("minecraft:entity_type",
                            out var requested) ||
                        requested.ValueKind != JsonValueKind.String)
                        throw new NotSupportedException(
                            $"Unsupported damage source entity selector {entry.Name}.");
                    string id = requested.GetString()!;
                    bool matches = id.StartsWith("#", StringComparison.Ordinal)
                        ? ReadTag("entity_type", id).Contains(
                            observed, StringComparer.Ordinal)
                        : id == observed;
                    if (!matches) return false;
                    break;
                }
                default:
                    throw new NotSupportedException(
                        $"Unsupported damage source predicate {entry.Name}.");
            }
        }
        return true;
    }

    private static void SetLootName(JsonElement modifier, List<Drop> drops)
    {
        string target = modifier.TryGetProperty("target", out var destination)
            ? destination.GetString() ?? "" : "custom_name";
        if (target is not ("item_name" or "custom_name"))
            throw new NotSupportedException("Unsupported 26.3 set_name target " + target);
        JsonElement name = modifier.GetProperty("name");
        string text;
        if (name.ValueKind == JsonValueKind.String)
            text = name.GetString()!;
        else if (name.ValueKind == JsonValueKind.Object &&
                 name.EnumerateObject().Count() == 1 &&
                 (name.TryGetProperty("translate", out var translatable) &&
                  translatable.ValueKind == JsonValueKind.String ||
                  name.TryGetProperty("text", out var literal) &&
                  literal.ValueKind == JsonValueKind.String))
            text = name.GetRawText();
        else
            throw new NotSupportedException(
                "set_name requires a plain, literal, or translated text component.");
        if (text.Length > 4096)
            throw new InvalidDataException("Loot text component too long.");
        foreach (Drop drop in drops)
            drop.Components["minecraft:" + target] = JsonSerializer.Serialize(text);
    }
}
