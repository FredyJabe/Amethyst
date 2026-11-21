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
                case "minecraft:ominous_bottle_amplifier":
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
                case "minecraft:block_state":
                {
                    if (value.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Item block_state must be a mapping.");
                    var properties = value.EnumerateObject().Select(
                        p => (p.Name, Value: p.Value.GetString()!)).ToArray();
                    if (properties.Length > 64)
                        throw new InvalidDataException("Too many block state properties.");
                    var payload = new PacketWriter();
                    payload.WriteVarInt(properties.Length);
                    foreach (var (property, state) in properties)
                    {
                        payload.WriteString(property);
                        payload.WriteString(state);
                    }
                    byte[] encodedState = payload.ToArray();
                    added[id] = w => w.WriteBytes(encodedState);
                    break;
                }
                case "minecraft:potion_contents":
                {
                    var payloadWriter = new PacketWriter();
                    WritePotionContents(payloadWriter, value);
                    byte[] potionPayload = payloadWriter.ToArray();
                    added[id] = w => w.WriteBytes(potionPayload);
                    break;
                }
                case "minecraft:potion_duration_scale":
                {
                    float scale = value.GetSingle();
                    if (!float.IsFinite(scale) || scale < 0)
                        throw new InvalidDataException("Invalid potion duration scale.");
                    added[id] = w => w.WriteFloat(scale);
                    break;
                }
                case "minecraft:suspicious_stew_effects":
                {
                    if (value.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("Stew effects must be an array.");
                    var entries = value.EnumerateArray().ToArray();
                    if (entries.Length > 16)
                        throw new InvalidDataException("Too many suspicious stew effects.");
                    var payloadWriter = new PacketWriter();
                    payloadWriter.WriteVarInt(entries.Length);
                    foreach (var entry in entries)
                    {
                        string effect = entry.GetProperty("id").GetString() ??
                            throw new InvalidDataException("Invalid stew effect ID.");
                        payloadWriter.WriteVarInt(
                            VanillaBuiltinIds.IndexOf("minecraft:mob_effect", effect));
                        int duration = entry.GetProperty("duration").GetInt32();
                        if (duration is < 0 or > 72000)
                            throw new InvalidDataException("Invalid stew effect duration.");
                        payloadWriter.WriteVarInt(duration);
                    }
                    byte[] stewPayload = payloadWriter.ToArray();
                    added[id] = w => w.WriteBytes(stewPayload);
                    break;
                }
                case "minecraft:enchantments":
                case "minecraft:stored_enchantments":
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

    /// <summary>Java 26.3 potion_contents STREAM_CODEC, including optional
    /// vanilla potion holder, custom color, effects and custom name.
    /// All network ids come from the pinned 777 static registries.</summary>
    private static void WritePotionContents(PacketWriter writer, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Potion contents must be an object.");
        foreach (JsonProperty entry in value.EnumerateObject())
            if (entry.Name is not ("potion" or "custom_color" or
                "custom_effects" or "custom_name"))
                throw new InvalidDataException(
                    $"Unknown 26.3 potion contents property {entry.Name}.");
        bool hasPotion = value.TryGetProperty("potion", out var potion);
        writer.WriteBool(hasPotion);
        if (hasPotion)
            writer.WriteVarInt(VanillaBuiltinIds.IndexOf(
                "minecraft:potion", potion.GetString()!));
        bool hasColor = value.TryGetProperty("custom_color", out var color);
        writer.WriteBool(hasColor);
        if (hasColor) writer.WriteInt32(color.GetInt32());
        JsonElement[] customEffects =
            value.TryGetProperty("custom_effects", out var effectsValue)
                ? effectsValue.EnumerateArray().ToArray() : [];
        if (customEffects.Length > 64)
            throw new InvalidDataException("Too many potion effects.");
        writer.WriteVarInt(customEffects.Length);
        foreach (JsonElement effect in customEffects)
        {
            writer.WriteVarInt(VanillaBuiltinIds.IndexOf(
                "minecraft:mob_effect",
                effect.GetProperty("id").GetString()!));
            WritePotionEffectDetails(writer, effect, 0);
        }
        bool hasName = value.TryGetProperty("custom_name", out var name);
        writer.WriteBool(hasName);
        if (hasName)
        {
            string text = name.GetString() ?? "";
            if (text.Length > 256)
                throw new InvalidDataException("Potion custom name too long.");
            writer.WriteString(text);
        }
    }

    private static void WritePotionEffectDetails(
        PacketWriter writer, JsonElement value, int depth)
    {
        if (depth > 8 || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid nested potion effect.");
        int amplifier = value.TryGetProperty("amplifier", out var amp)
            ? amp.GetInt32() : 0;
        int duration = value.TryGetProperty("duration", out var dur)
            ? dur.GetInt32() : 0;
        if (amplifier is < 0 or > 255 || duration is < 0 or > 72000)
            throw new InvalidDataException("Invalid potion effect value.");
        writer.WriteVarInt(amplifier);
        writer.WriteVarInt(duration);
        writer.WriteBool(value.TryGetProperty("ambient", out var ambient) &&
            ambient.GetBoolean());
        writer.WriteBool(!value.TryGetProperty("show_particles", out var particles) ||
            particles.GetBoolean());
        writer.WriteBool(!value.TryGetProperty("show_icon", out var icon) ||
            icon.GetBoolean());
        bool hidden = value.TryGetProperty("hidden_effect", out var child);
        writer.WriteBool(hidden);
        if (hidden) WritePotionEffectDetails(writer, child, depth + 1);
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
        // Native loot set_name can return a translatable text component.
        // Preserve the translation key as NBT instead of displaying its raw
        // JSON or treating the untranslated key as a literal item name.
        if (value.StartsWith("{", StringComparison.Ordinal) &&
            (value.Contains("\\"translate\\"", StringComparison.Ordinal) ||
             value.Contains("\\"text\\"", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(value);
            JsonElement component = document.RootElement;
            if (component.ValueKind == JsonValueKind.Object &&
                component.EnumerateObject().Count() == 1)
            {
                string field = component.TryGetProperty("translate", out var translated)
                    ? "translate" : "text";
                if (component.TryGetProperty(field, out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    content.GetString() is string text && text.Length <= 4096)
                {
                    writer.WriteByte(10); // unnamed TAG_Compound text component
                    WriteNbtString(writer, field, text);
                    writer.WriteByte(0); // TAG_End
                    return;
                }
            }
            throw new InvalidDataException(
                "Unsupported structured item text component.");
        }
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
