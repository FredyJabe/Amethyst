# Minecraft Java 26.3 item components for Amethyst

This is the complete **122-entry** `minecraft:data_component_type` registry used by Minecraft Java **26.3 (protocol 777)**, as declared in `src/Shards/ShardItemComponentIds.cs`. Numeric IDs here are **wire indices**, not item IDs and not stable across versions.

**Supported** means an administrator can currently write the field under an Items Shard `Components:` mapping and Amethyst serializes it. **Not yet supported** means the type exists in the Minecraft registry but needs a typed parser/codec and, in some cases, gameplay before it can be safely configured. Do not copy the full table into executable YAML; unimplemented fields deliberately fail startup.

## Example YAML

Write a custom ID as the root key and an existing vanilla item in the `Id` field. The alias uses the vanilla item protocol ID and is **not independently distinguishable on stacks** yet:

```yaml
legendary_pickaxe:
  Id: minecraft:diamond_pickaxe
  ItemModel: amethyst:legendary_pickaxe
  Display: "<gold>Legendary Pickaxe</gold>"
  Components:
    minecraft:rarity: epic
    minecraft:enchantment_glint_override: true
```

To actually display these components in inventory today, override the **canonical vanilla root** (e.g. `diamond_pickaxe:`) instead of a custom alias. The `ItemModel` value is a namespaced resource location and requires the corresponding item-model asset in a client resource pack.

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

## Complete component registry

### ID 0–22: core and presentation

| Registry ID | Component | YAML serialization |
| ---: | --- | --- |
| 0 | `minecraft:custom_data` | Not yet supported |
| 1 | `minecraft:max_stack_size` | Supported |
| 2 | `minecraft:max_damage` | Not yet supported |
| 3 | `minecraft:damage` | Not yet supported |
| 4 | `minecraft:unbreakable` | Supported |
| 5 | `minecraft:use_effects` | Not yet supported |
| 6 | `minecraft:custom_name` | Supported |
| 7 | `minecraft:minimum_attack_charge` | Not yet supported |
| 8 | `minecraft:damage_type` | Not yet supported |
| 9 | `minecraft:item_name` | Supported |
| 10 | `minecraft:item_model` | Supported |
| 11 | `minecraft:lore` | Supported |
| 12 | `minecraft:rarity` | Supported |
| 13 | `minecraft:enchantments` | Not yet supported |
| 14 | `minecraft:can_place_on` | Not yet supported |
| 15 | `minecraft:can_break` | Not yet supported |
| 16 | `minecraft:attribute_modifiers` | Not yet supported |
| 17 | `minecraft:custom_model_data` | Supported |
| 18 | `minecraft:tooltip_display` | Supported |
| 19 | `minecraft:repair_cost` | Supported |
| 20 | `minecraft:creative_slot_lock` | Not yet supported |
| 21 | `minecraft:enchantment_glint_override` | Supported |
| 22 | `minecraft:intangible_projectile` | Not yet supported |

### ID 23–45: use, equipment, and interactions

| Registry ID | Component | YAML serialization |
| ---: | --- | --- |
| 23 | `minecraft:food` | Not yet supported |
| 24 | `minecraft:consumable` | Not yet supported |
| 25 | `minecraft:use_remainder` | Not yet supported |
| 26 | `minecraft:use_cooldown` | Not yet supported |
| 27 | `minecraft:damage_resistant` | Not yet supported |
| 28 | `minecraft:tool` | Not yet supported |
| 29 | `minecraft:weapon` | Not yet supported |
| 30 | `minecraft:attack_range` | Not yet supported |
| 31 | `minecraft:enchantable` | Not yet supported |
| 32 | `minecraft:equippable` | Not yet supported |
| 33 | `minecraft:repairable` | Not yet supported |
| 34 | `minecraft:glider` | Not yet supported |
| 35 | `minecraft:tooltip_style` | Supported |
| 36 | `minecraft:death_protection` | Not yet supported |
| 37 | `minecraft:blocks_attacks` | Not yet supported |
| 38 | `minecraft:piercing_weapon` | Not yet supported |
| 39 | `minecraft:kinetic_weapon` | Not yet supported |
| 40 | `minecraft:attack_animation` | Not yet supported |
| 41 | `minecraft:interact_animation` | Not yet supported |
| 42 | `minecraft:additional_trade_cost` | Not yet supported |
| 43 | `minecraft:block_transformer` | Not yet supported |
| 44 | `minecraft:villager_food` | Not yet supported |
| 45 | `minecraft:stored_enchantments` | Not yet supported |

### ID 46–86: containers, stored data, and content

| Registry ID | Component | YAML serialization |
| ---: | --- | --- |
| 46 | `minecraft:dye` | Not yet supported |
| 47 | `minecraft:dyed_color` | Not yet supported |
| 48 | `minecraft:map_id` | Not yet supported |
| 49 | `minecraft:map_decorations` | Not yet supported |
| 50 | `minecraft:map_post_processing` | Not yet supported |
| 51 | `minecraft:charged_projectiles` | Not yet supported |
| 52 | `minecraft:bundle_contents` | Not yet supported |
| 53 | `minecraft:potion_contents` | Not yet supported |
| 54 | `minecraft:potion_duration_scale` | Not yet supported |
| 55 | `minecraft:suspicious_stew_effects` | Not yet supported |
| 56 | `minecraft:writable_book_content` | Not yet supported |
| 57 | `minecraft:written_book_content` | Not yet supported |
| 58 | `minecraft:trim` | Not yet supported |
| 59 | `minecraft:debug_stick_state` | Not yet supported |
| 60 | `minecraft:entity_data` | Not yet supported |
| 61 | `minecraft:bucket_entity_data` | Not yet supported |
| 62 | `minecraft:block_entity_data` | Not yet supported |
| 63 | `minecraft:instrument` | Not yet supported |
| 64 | `minecraft:provides_trim_material` | Not yet supported |
| 65 | `minecraft:ominous_bottle_amplifier` | Not yet supported |
| 66 | `minecraft:jukebox_playable` | Not yet supported |
| 67 | `minecraft:provides_banner_patterns` | Not yet supported |
| 68 | `minecraft:recipes` | Not yet supported |
| 69 | `minecraft:lodestone_tracker` | Not yet supported |
| 70 | `minecraft:firework_explosion` | Not yet supported |
| 71 | `minecraft:fireworks` | Not yet supported |
| 72 | `minecraft:profile` | Not yet supported |
| 73 | `minecraft:note_block_sound` | Not yet supported |
| 74 | `minecraft:banner_patterns` | Not yet supported |
| 75 | `minecraft:base_color` | Not yet supported |
| 76 | `minecraft:pot_decorations` | Not yet supported |
| 77 | `minecraft:container` | Not yet supported |
| 78 | `minecraft:block_state` | Not yet supported |
| 79 | `minecraft:bees` | Not yet supported |
| 80 | `minecraft:sulfur_cube_content` | Not yet supported |
| 81 | `minecraft:lock` | Not yet supported |
| 82 | `minecraft:container_loot` | Not yet supported |
| 83 | `minecraft:break_sound` | Not yet supported |
| 84 | `minecraft:compostable` | Not yet supported |
| 85 | `minecraft:cooking_fuel` | Not yet supported |
| 86 | `minecraft:brewing_fuel` | Not yet supported |

### ID 87–121: variants and remaining components

| Registry ID | Component | YAML serialization |
| ---: | --- | --- |
| 87 | `minecraft:mob_visibility` | Not yet supported |
| 88 | `minecraft:villager/variant` | Not yet supported |
| 89 | `minecraft:wolf/variant` | Not yet supported |
| 90 | `minecraft:wolf/sound_variant` | Not yet supported |
| 91 | `minecraft:wolf/collar` | Not yet supported |
| 92 | `minecraft:fox/variant` | Not yet supported |
| 93 | `minecraft:salmon/size` | Not yet supported |
| 94 | `minecraft:parrot/variant` | Not yet supported |
| 95 | `minecraft:tropical_fish/pattern` | Not yet supported |
| 96 | `minecraft:tropical_fish/base_color` | Not yet supported |
| 97 | `minecraft:tropical_fish/pattern_color` | Not yet supported |
| 98 | `minecraft:mooshroom/variant` | Not yet supported |
| 99 | `minecraft:rabbit/variant` | Not yet supported |
| 100 | `minecraft:pig/variant` | Not yet supported |
| 101 | `minecraft:pig/sound_variant` | Not yet supported |
| 102 | `minecraft:cow/variant` | Not yet supported |
| 103 | `minecraft:cow/sound_variant` | Not yet supported |
| 104 | `minecraft:chicken/variant` | Not yet supported |
| 105 | `minecraft:chicken/sound_variant` | Not yet supported |
| 106 | `minecraft:zombie_nautilus/variant` | Not yet supported |
| 107 | `minecraft:frog/variant` | Not yet supported |
| 108 | `minecraft:horse/variant` | Not yet supported |
| 109 | `minecraft:painting/variant` | Not yet supported |
| 110 | `minecraft:llama/variant` | Not yet supported |
| 111 | `minecraft:axolotl/variant` | Not yet supported |
| 112 | `minecraft:cat/variant` | Not yet supported |
| 113 | `minecraft:cat/sound_variant` | Not yet supported |
| 114 | `minecraft:cat/collar` | Not yet supported |
| 115 | `minecraft:sheep/color` | Not yet supported |
| 116 | `minecraft:shulker/color` | Not yet supported |
| 117 | `minecraft:provides_pottery_pattern` | Not yet supported |
| 118 | `minecraft:sign_text_front` | Not yet supported |
| 119 | `minecraft:sign_text_back` | Not yet supported |
| 120 | `minecraft:waxed` | Not yet supported |
| 121 | `minecraft:cushion/color` | Not yet supported |

**Total: 122 components, 12 with implemented definition codecs.**

Minecraft reference: [Data component format](https://minecraft.wiki/w/Data_component_format).
