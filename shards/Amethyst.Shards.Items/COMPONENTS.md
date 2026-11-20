# Minecraft Java 26.3 item components for Amethyst

This is the complete **122-entry** `minecraft:data_component_type` registry used by Minecraft Java **26.3 (protocol 777)**, as declared in `src/Shards/ShardItemComponentIds.cs`. Numeric IDs here are **wire indices**, not item IDs and not stable across versions.

The registry has **122 item component types**. Exactly **45 have
user-approved, fixed YAML node names**; the other **77 have no shorthand
names assigned**. These 45 assignments are hard-coded in
`ItemComponentNodeMap.cs` and **cannot be changed through a YAML mapping
file**. The full list below shows the YAML name for every component:
`(pending)` means no YAML node name has been selected yet.

Every component remains addressable with its canonical `minecraft:`
identifier inside an item's `Components:` mapping. Short node names
are valid only for the approved 45 entries. In the value format column,
**Typed** means Amethyst validates and writes a normal YAML value;
**WireHex / Remove** means the component needs an already-encoded
protocol value or a removal marker, pending a typed codec and potentially
server-side gameplay.

## Example item YAML

```yaml
diamond_pickaxe:
  Id: minecraft:diamond_pickaxe
  CustomName: "<gold>Amethyst Pickaxe</gold>"
  ItemModel: amethyst:legendary_pickaxe
  Components:
    Rarity: epic
    GlintOverride: true

stone:
  Options:
    CustomData: { WireHex: "0A00" }
    Glider: { Remove: true }
```

`WireHex` is an advanced protocol-level escape hatch, not automatic
semantic support. The example bytes are illustrative only; incorrect
payloads may disconnect clients. You can list all mappings on the server
with `componentnodes` or `componentnodes food`.

Custom aliases still use the base item's vanilla protocol ID; they
are not separate stack identities. Custom models require the matching
client resource pack.

## Supported component shapes

| Component | YAML example |
| --- | --- |
| `minecraft:item_model` | `amethyst:my_model` |
| `minecraft:tooltip_style` | `amethyst:my_tooltip` |
| `minecraft:custom_name` | `"<aqua>My item</aqua>"` |
| `minecraft:item_name` | `"My item"` |
| `minecraft:lore` | `["Line one", "Line two"]` |
| `minecraft:rarity` | `epic` |
| `minecraft:enchantment_glint_override` | `true` |
| `minecraft:unbreakable` | `true` |
| `minecraft:repair_cost` | `4` |
| `minecraft:max_stack_size` | `16` (vanilla-capacity constraint applies) |
| `minecraft:custom_model_data` | `{floats: [42.0], flags: [], strings: [], colors: []}` |
| `minecraft:tooltip_display` | `{hide_tooltip: false, hidden_components: [minecraft:enchantments]}` |
| `minecraft:max_damage`, `damage`, `enchantable`, `map_id` | `120` (nonnegative integer) |
| `minecraft:dyed_color` | `16711935` (RGB integer) |
| `minecraft:creative_slot_lock`, `intangible_projectile`, `glider`, `waxed` | `true` or `false` |

## Complete component registry

### ID 0–22: core and presentation

| Registry ID | Component | Fixed YAML node | Value format |
| ---: | --- | --- | --- |
| 0 | `minecraft:custom_data` | `CustomData` | WireHex / Remove |
| 1 | `minecraft:max_stack_size` | `StackSize` | Typed |
| 2 | `minecraft:max_damage` | `DurabilityMax` | Typed |
| 3 | `minecraft:damage` | `DurabilityDamage` | Typed |
| 4 | `minecraft:unbreakable` | `Unbreakable` | Typed |
| 5 | `minecraft:use_effects` | *(pending)* | WireHex / Remove |
| 6 | `minecraft:custom_name` | `CustomName` | Typed |
| 7 | `minecraft:minimum_attack_charge` | `MinimumAttackCharge` | WireHex / Remove |
| 8 | `minecraft:damage_type` | `DamageType` | WireHex / Remove |
| 9 | `minecraft:item_name` | `ItemName` | Typed |
| 10 | `minecraft:item_model` | `ItemModel` | Typed |
| 11 | `minecraft:lore` | `Lore` | Typed |
| 12 | `minecraft:rarity` | `Rarity` | Typed |
| 13 | `minecraft:enchantments` | `Enchantments` | WireHex / Remove |
| 14 | `minecraft:can_place_on` | `CanPlaceOn` | WireHex / Remove |
| 15 | `minecraft:can_break` | `CanBreak` | WireHex / Remove |
| 16 | `minecraft:attribute_modifiers` | `AttributesModifiers` | WireHex / Remove |
| 17 | `minecraft:custom_model_data` | `CustomModelData` | Typed |
| 18 | `minecraft:tooltip_display` | `TooltipDisplay` | Typed |
| 19 | `minecraft:repair_cost` | `RepairCost` | Typed |
| 20 | `minecraft:creative_slot_lock` | `CreativeSlotLock` | Typed |
| 21 | `minecraft:enchantment_glint_override` | `GlintOverride` | Typed |
| 22 | `minecraft:intangible_projectile` | *(pending)* | Typed |

### ID 23–45: use, equipment, and interactions

| Registry ID | Component | Fixed YAML node | Value format |
| ---: | --- | --- | --- |
| 23 | `minecraft:food` | `Food` | WireHex / Remove |
| 24 | `minecraft:consumable` | `Consumable` | WireHex / Remove |
| 25 | `minecraft:use_remainder` | `UseRemainder` | WireHex / Remove |
| 26 | `minecraft:use_cooldown` | `UseCooldown` | WireHex / Remove |
| 27 | `minecraft:damage_resistant` | `DamageResistant` | WireHex / Remove |
| 28 | `minecraft:tool` | `Tool` | WireHex / Remove |
| 29 | `minecraft:weapon` | `Weapon` | WireHex / Remove |
| 30 | `minecraft:attack_range` | `AttackRange` | WireHex / Remove |
| 31 | `minecraft:enchantable` | `Enchantable` | Typed |
| 32 | `minecraft:equippable` | `Equippable` | WireHex / Remove |
| 33 | `minecraft:repairable` | `Repairable` | WireHex / Remove |
| 34 | `minecraft:glider` | `Glider` | Typed |
| 35 | `minecraft:tooltip_style` | `TooltipStyle` | Typed |
| 36 | `minecraft:death_protection` | `DeathProtection` | WireHex / Remove |
| 37 | `minecraft:blocks_attacks` | `BlocksAttacks` | WireHex / Remove |
| 38 | `minecraft:piercing_weapon` | `PiercingWeapon` | WireHex / Remove |
| 39 | `minecraft:kinetic_weapon` | `KineticWeapon` | WireHex / Remove |
| 40 | `minecraft:attack_animation` | *(pending)* | WireHex / Remove |
| 41 | `minecraft:interact_animation` | *(pending)* | WireHex / Remove |
| 42 | `minecraft:additional_trade_cost` | *(pending)* | WireHex / Remove |
| 43 | `minecraft:block_transformer` | *(pending)* | WireHex / Remove |
| 44 | `minecraft:villager_food` | *(pending)* | WireHex / Remove |
| 45 | `minecraft:stored_enchantments` | *(pending)* | WireHex / Remove |

### ID 46–86: containers, stored data, and content

| Registry ID | Component | Fixed YAML node | Value format |
| ---: | --- | --- | --- |
| 46 | `minecraft:dye` | *(pending)* | WireHex / Remove |
| 47 | `minecraft:dyed_color` | `DyedColor` | Typed |
| 48 | `minecraft:map_id` | *(pending)* | Typed |
| 49 | `minecraft:map_decorations` | *(pending)* | WireHex / Remove |
| 50 | `minecraft:map_post_processing` | *(pending)* | WireHex / Remove |
| 51 | `minecraft:charged_projectiles` | *(pending)* | WireHex / Remove |
| 52 | `minecraft:bundle_contents` | *(pending)* | WireHex / Remove |
| 53 | `minecraft:potion_contents` | *(pending)* | WireHex / Remove |
| 54 | `minecraft:potion_duration_scale` | *(pending)* | WireHex / Remove |
| 55 | `minecraft:suspicious_stew_effects` | *(pending)* | WireHex / Remove |
| 56 | `minecraft:writable_book_content` | *(pending)* | WireHex / Remove |
| 57 | `minecraft:written_book_content` | *(pending)* | WireHex / Remove |
| 58 | `minecraft:trim` | *(pending)* | WireHex / Remove |
| 59 | `minecraft:debug_stick_state` | *(pending)* | WireHex / Remove |
| 60 | `minecraft:entity_data` | *(pending)* | WireHex / Remove |
| 61 | `minecraft:bucket_entity_data` | *(pending)* | WireHex / Remove |
| 62 | `minecraft:block_entity_data` | *(pending)* | WireHex / Remove |
| 63 | `minecraft:instrument` | `Instrument` | WireHex / Remove |
| 64 | `minecraft:provides_trim_material` | *(pending)* | WireHex / Remove |
| 65 | `minecraft:ominous_bottle_amplifier` | *(pending)* | WireHex / Remove |
| 66 | `minecraft:jukebox_playable` | `JukeboxPlayable` | WireHex / Remove |
| 67 | `minecraft:provides_banner_patterns` | *(pending)* | WireHex / Remove |
| 68 | `minecraft:recipes` | *(pending)* | WireHex / Remove |
| 69 | `minecraft:lodestone_tracker` | *(pending)* | WireHex / Remove |
| 70 | `minecraft:firework_explosion` | *(pending)* | WireHex / Remove |
| 71 | `minecraft:fireworks` | *(pending)* | WireHex / Remove |
| 72 | `minecraft:profile` | *(pending)* | WireHex / Remove |
| 73 | `minecraft:note_block_sound` | *(pending)* | WireHex / Remove |
| 74 | `minecraft:banner_patterns` | *(pending)* | WireHex / Remove |
| 75 | `minecraft:base_color` | *(pending)* | WireHex / Remove |
| 76 | `minecraft:pot_decorations` | *(pending)* | WireHex / Remove |
| 77 | `minecraft:container` | *(pending)* | WireHex / Remove |
| 78 | `minecraft:block_state` | *(pending)* | WireHex / Remove |
| 79 | `minecraft:bees` | *(pending)* | WireHex / Remove |
| 80 | `minecraft:sulfur_cube_content` | *(pending)* | WireHex / Remove |
| 81 | `minecraft:lock` | `Lock` | WireHex / Remove |
| 82 | `minecraft:container_loot` | *(pending)* | WireHex / Remove |
| 83 | `minecraft:break_sound` | *(pending)* | WireHex / Remove |
| 84 | `minecraft:compostable` | `Compostable` | WireHex / Remove |
| 85 | `minecraft:cooking_fuel` | `CookingFuel` | WireHex / Remove |
| 86 | `minecraft:brewing_fuel` | `BrewingFuel` | WireHex / Remove |

### ID 87–121: variants and remaining components

| Registry ID | Component | Fixed YAML node | Value format |
| ---: | --- | --- | --- |
| 87 | `minecraft:mob_visibility` | *(pending)* | WireHex / Remove |
| 88 | `minecraft:villager/variant` | *(pending)* | WireHex / Remove |
| 89 | `minecraft:wolf/variant` | *(pending)* | WireHex / Remove |
| 90 | `minecraft:wolf/sound_variant` | *(pending)* | WireHex / Remove |
| 91 | `minecraft:wolf/collar` | *(pending)* | WireHex / Remove |
| 92 | `minecraft:fox/variant` | *(pending)* | WireHex / Remove |
| 93 | `minecraft:salmon/size` | *(pending)* | WireHex / Remove |
| 94 | `minecraft:parrot/variant` | *(pending)* | WireHex / Remove |
| 95 | `minecraft:tropical_fish/pattern` | *(pending)* | WireHex / Remove |
| 96 | `minecraft:tropical_fish/base_color` | *(pending)* | WireHex / Remove |
| 97 | `minecraft:tropical_fish/pattern_color` | *(pending)* | WireHex / Remove |
| 98 | `minecraft:mooshroom/variant` | *(pending)* | WireHex / Remove |
| 99 | `minecraft:rabbit/variant` | *(pending)* | WireHex / Remove |
| 100 | `minecraft:pig/variant` | *(pending)* | WireHex / Remove |
| 101 | `minecraft:pig/sound_variant` | *(pending)* | WireHex / Remove |
| 102 | `minecraft:cow/variant` | *(pending)* | WireHex / Remove |
| 103 | `minecraft:cow/sound_variant` | *(pending)* | WireHex / Remove |
| 104 | `minecraft:chicken/variant` | *(pending)* | WireHex / Remove |
| 105 | `minecraft:chicken/sound_variant` | *(pending)* | WireHex / Remove |
| 106 | `minecraft:zombie_nautilus/variant` | *(pending)* | WireHex / Remove |
| 107 | `minecraft:frog/variant` | *(pending)* | WireHex / Remove |
| 108 | `minecraft:horse/variant` | *(pending)* | WireHex / Remove |
| 109 | `minecraft:painting/variant` | *(pending)* | WireHex / Remove |
| 110 | `minecraft:llama/variant` | *(pending)* | WireHex / Remove |
| 111 | `minecraft:axolotl/variant` | *(pending)* | WireHex / Remove |
| 112 | `minecraft:cat/variant` | *(pending)* | WireHex / Remove |
| 113 | `minecraft:cat/sound_variant` | *(pending)* | WireHex / Remove |
| 114 | `minecraft:cat/collar` | *(pending)* | WireHex / Remove |
| 115 | `minecraft:sheep/color` | *(pending)* | WireHex / Remove |
| 116 | `minecraft:shulker/color` | *(pending)* | WireHex / Remove |
| 117 | `minecraft:provides_pottery_pattern` | *(pending)* | WireHex / Remove |
| 118 | `minecraft:sign_text_front` | *(pending)* | WireHex / Remove |
| 119 | `minecraft:sign_text_back` | *(pending)* | WireHex / Remove |
| 120 | `minecraft:waxed` | *(pending)* | Typed |
| 121 | `minecraft:cushion/color` | *(pending)* | WireHex / Remove |

**Total: 122 registered components; 45 fixed YAML names; 77 names pending; 21 typed value codecs.**

Minecraft reference: [Data component format](https://minecraft.wiki/w/Data_component_format).
