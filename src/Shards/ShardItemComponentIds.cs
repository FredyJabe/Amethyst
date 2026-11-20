namespace Amethyst.Shards;

/// <summary>Data component type registry IDs for Minecraft Java 26.3
/// (protocol 777). These are protocol IDs, never stable across versions.
/// The existence of an ID does not imply Gameplay supports writing its
/// complex codec.</summary>
public static class ShardItemComponentIds
{
    private static readonly string[] Names = """
custom_data
max_stack_size
max_damage
damage
unbreakable
use_effects
custom_name
minimum_attack_charge
damage_type
item_name
item_model
lore
rarity
enchantments
can_place_on
can_break
attribute_modifiers
custom_model_data
tooltip_display
repair_cost
creative_slot_lock
enchantment_glint_override
intangible_projectile
food
consumable
use_remainder
use_cooldown
damage_resistant
tool
weapon
attack_range
enchantable
equippable
repairable
glider
tooltip_style
death_protection
blocks_attacks
piercing_weapon
kinetic_weapon
attack_animation
interact_animation
additional_trade_cost
block_transformer
villager_food
stored_enchantments
dye
dyed_color
map_id
map_decorations
map_post_processing
charged_projectiles
bundle_contents
potion_contents
potion_duration_scale
suspicious_stew_effects
writable_book_content
written_book_content
trim
debug_stick_state
entity_data
bucket_entity_data
block_entity_data
instrument
provides_trim_material
ominous_bottle_amplifier
jukebox_playable
provides_banner_patterns
recipes
lodestone_tracker
firework_explosion
fireworks
profile
note_block_sound
banner_patterns
base_color
pot_decorations
container
block_state
bees
sulfur_cube_content
lock
container_loot
break_sound
compostable
cooking_fuel
brewing_fuel
mob_visibility
villager/variant
wolf/variant
wolf/sound_variant
wolf/collar
fox/variant
salmon/size
parrot/variant
tropical_fish/pattern
tropical_fish/base_color
tropical_fish/pattern_color
mooshroom/variant
rabbit/variant
pig/variant
pig/sound_variant
cow/variant
cow/sound_variant
chicken/variant
chicken/sound_variant
zombie_nautilus/variant
frog/variant
horse/variant
painting/variant
llama/variant
axolotl/variant
cat/variant
cat/sound_variant
cat/collar
sheep/color
shulker/color
provides_pottery_pattern
sign_text_front
sign_text_back
waxed
cushion/color
""".Split('\n', StringSplitOptions.RemoveEmptyEntries |
        StringSplitOptions.TrimEntries);

    private static readonly IReadOnlyDictionary<string, int> ByName =
        Names.Select((name, index) => (name, index))
            .ToDictionary(pair => "minecraft:" + pair.name,
                pair => pair.index, StringComparer.Ordinal);

    /// <summary>Every component type supported by the pinned Java 26.3
    /// registry, in protocol-ID order. Data formats vary by type.</summary>
    public static IReadOnlyList<string> All { get; } =
        Array.AsReadOnly(Names.Select(name => "minecraft:" + name).ToArray());

    public static int Count => Names.Length;

    public static bool TryGetIdentifier(int protocolId, out string identifier)
    {
        if ((uint)protocolId < (uint)All.Count)
        {
            identifier = All[protocolId];
            return true;
        }
        identifier = string.Empty;
        return false;
    }

    public static bool TryGetId(string identifier, out int protocolId)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            protocolId = -1;
            return false;
        }
        string key = identifier.Contains(':') ? identifier :
            "minecraft:" + identifier;
        return ByName.TryGetValue(key, out protocolId);
    }
}
