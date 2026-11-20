using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amethyst.Shards;

namespace Amethyst;

/// <summary>Serializes validated 26.3 definition component patches into
/// clientbound Slot format. Stack-specific wear/enchantments are written by
/// InventoryProtocol after the definition patch. All send paths (menus, entity
/// metadata, inventory) use the same codec.</summary>
internal static class ItemComponentWire
{
    public static void AddDefinition(
        IDictionary<int, Action<PacketWriter>> added, ISet<int> removed,
        ShardItemDefinition definition)
    {
        if (definition.Components is null) return;
        AddComponents(added, removed, definition.Components);
    }

    /// <summary>Apply canonical per-stack patches over definition defaults.
    /// A removal overrides a previously added definition component.</summary>
    public static void AddComponents(
        IDictionary<int, Action<PacketWriter>> added, ISet<int> removed,
        IReadOnlyDictionary<string, string> components)
    {
        foreach (var (name, json) in components)
        {
            if (!ShardItemComponentIds.TryGetId(name, out int id))
                throw new InvalidDataException($"Unknown component {name}.");
            using var document = JsonDocument.Parse(json);
            JsonElement value = document.RootElement;
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("WireHex", out JsonElement encoded))
            {
                byte[] payload = Convert.FromHexString(encoded.GetString()!);
                removed.Remove(id);
                added[id] = writer => writer.WriteBytes(payload);
                continue;
            }
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("Remove", out JsonElement remove))
            {
                if (!remove.GetBoolean())
                    throw new InvalidDataException($"Invalid removal for {name}.");
                added.Remove(id);
                removed.Add(id);
                continue;
            }
            removed.Remove(id);
            switch (name)
            {
                case "minecraft:unbreakable":
                    if (value.GetBoolean()) added[id] = static _ => { };
                    else removed.Add(id);
                    break;
                case "minecraft:max_stack_size":
                case "minecraft:repair_cost":
                case "minecraft:max_damage":
                case "minecraft:damage":
                case "minecraft:enchantable":
                case "minecraft:map_id":
                    int number = value.GetInt32();
                    added[id] = w => w.WriteVarInt(number);
                    break;
                case "minecraft:dyed_color":
                    int color = value.GetInt32();
                    added[id] = w => w.WriteInt32(color);
                    break;
                case "minecraft:creative_slot_lock":
                case "minecraft:intangible_projectile":
                case "minecraft:glider":
                case "minecraft:waxed":
                    if (value.GetBoolean()) added[id] = static _ => { };
                    else removed.Add(id);
                    break;
                case "minecraft:item_model":
                case "minecraft:tooltip_style":
                    string identifier = value.GetString()!;
                    added[id] = w => w.WriteString(identifier);
                    break;
                case "minecraft:custom_name":
                case "minecraft:item_name":
                    string nameText = value.GetString()!;
                    added[id] = w => WriteText(w, nameText);
                    break;
                case "minecraft:lore":
                    string[] lines = value.EnumerateArray()
                        .Select(e => e.GetString()!).ToArray();
                    added[id] = w =>
                    {
                        w.WriteVarInt(lines.Length);
                        foreach (string line in lines) WriteText(w, line);
                    };
                    break;
                case "minecraft:rarity":
                    int rarity = value.GetString() switch
                    {
                        "common" => 0, "uncommon" => 1,
                        "rare" => 2, "epic" => 3,
                        _ => throw new InvalidDataException("Invalid rarity.")
                    };
                    added[id] = w => w.WriteVarInt(rarity);
                    break;
                case "minecraft:enchantment_glint_override":
                    bool glint = value.GetBoolean();
                    added[id] = w => w.WriteBool(glint);
                    break;
                case "minecraft:custom_model_data":
                    float[] floats = value.GetProperty("floats").EnumerateArray()
                        .Select(e => e.GetSingle()).ToArray();
                    bool[] flags = value.GetProperty("flags").EnumerateArray()
                        .Select(e => e.GetBoolean()).ToArray();
                    string[] strings = value.GetProperty("strings").EnumerateArray()
                        .Select(e => e.GetString()!).ToArray();
                    int[] colors = value.GetProperty("colors").EnumerateArray()
                        .Select(e => e.GetInt32()).ToArray();
                    added[id] = w =>
                    {
                        w.WriteVarInt(floats.Length);
                        foreach (float entry in floats) w.WriteFloat(entry);
                        w.WriteVarInt(flags.Length);
                        foreach (bool entry in flags) w.WriteBool(entry);
                        w.WriteVarInt(strings.Length);
                        foreach (string entry in strings) w.WriteString(entry);
                        w.WriteVarInt(colors.Length);
                        foreach (int entry in colors) w.WriteInt32(entry);
                    };
                    break;
                case "minecraft:enchantments":
                    var levels = value.EnumerateObject()
                        .Select(property => (Id: VanillaRegistries.IndexOf(
                            "minecraft:enchantment", property.Name),
                            Level: property.Value.GetInt32()))
                        .OrderBy(p => p.Id).ToArray();
                    added[id] = w =>
                    {
                        w.WriteVarInt(levels.Length);
                        foreach (var (enchantmentId, level) in levels)
                        {
                            w.WriteVarInt(enchantmentId);
                            w.WriteVarInt(level);
                        }
                    };
                    break;
                case "minecraft:tooltip_display":
                    bool hide = value.GetProperty("hide_tooltip").GetBoolean();
                    int[] hidden = value.GetProperty("hidden_components")
                        .EnumerateArray().Select(e =>
                        {
                            if (!ShardItemComponentIds.TryGetId(e.GetString()!, out int hiddenId))
                                throw new InvalidDataException("Invalid hidden component.");
                            return hiddenId;
                        }).ToArray();
                    added[id] = w =>
                    {
                        w.WriteBool(hide);
                        w.WriteVarInt(hidden.Length);
                        foreach (int hiddenId in hidden) w.WriteVarInt(hiddenId);
                    };
                    break;
                default:
                    throw new InvalidDataException(
                        $"No 26.3 network serializer for configured component {name}; " +
                        "use WireHex or Remove in its YAML node.");
            }
        }
    }

    // 26.3 text components use unnamed network NBT. The common Mythic-style
    // <color>Text</color> shorthand is handled as a colored compound; other
    // strings remain literal NBT text components, not fake JSON-on-the-wire.
    private static readonly Regex ColoredText = new(
        @"^<(?<color>black|dark_blue|dark_green|dark_aqua|dark_red|dark_purple|gold|gray|dark_gray|blue|green|aqua|red|light_purple|yellow|white|#[0-9a-fA-F]{6})>(?<text>.*)</\k<color>>$",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant);

    private static void WriteText(PacketWriter writer, string value)
    {
        Match match = ColoredText.Match(value);
        if (!match.Success)
        {
            writer.WritePlainTextComponent(value);
            return;
        }
        writer.WriteByte(10); // unnamed TAG_Compound text component
        WriteNbtString(writer, "text", match.Groups["text"].Value);
        WriteNbtString(writer, "color", match.Groups["color"].Value.ToLowerInvariant());
        writer.WriteByte(1); // TAG_Byte
        WriteNbtUtf8(writer, "italic");
        writer.WriteByte(0); // false (vanilla custom-name italics disabled)
        writer.WriteByte(0); // TAG_End
    }

    private static void WriteNbtString(PacketWriter writer, string field, string value)
    {
        writer.WriteByte(8); // TAG_String
        WriteNbtUtf8(writer, field);
        WriteNbtUtf8(writer, value);
    }

    private static void WriteNbtUtf8(PacketWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > short.MaxValue)
            throw new InvalidDataException("NBT text too large.");
        writer.WriteInt16((short)bytes.Length);
        writer.WriteBytes(bytes);
    }
}
