# Minecraft Java 26.3 item components for Amethyst

This is the complete **122-entry** `minecraft:data_component_type` registry used by Minecraft Java **26.3 (protocol 777)**, as declared in `src/Shards/ShardItemComponentIds.cs`. Numeric IDs here are **wire indices**, not item IDs and not stable across versions.

Every component type is addressable from YAML using the configurable name in `shards/data/items/component-nodes.yml`, or its literal `minecraft:` registry identifier. **Typed** indicates Amethyst validates and writes a normal YAML value. **WireHex / Remove** indicates the component is available through a protocol-byte payload or removal marker, but the complex semantic YAML codec and (where appropriate) server gameplay are not yet implemented. A raw byte payload must use the exact Java 26.3 encoding: invalid bytes can disconnect or crash the client.

## YAML node mapping

Change the **value** on any of the 122 lines in
[component-nodes.yml](../../data/items/component-nodes.yml), e.g.:

```yaml
minecraft:item_model: MyItemModel
minecraft:custom_name: NameText
minecraft:food: FoodSettings
minecraft:glider: FlightMode
```

The Items Shard reads this mapping on startup and accepts these names
in an item's `Components:` or `Options:` mapping. Canonical
`minecraft:item_model` keys also remain valid. The configured map is
exposed to other Shards through `IShardItemRegistry.ComponentNodes`.
Run `componentnodes` in the console to list the effective mapping,
or `componentnodes food` to filter it.

```yaml
diamond_pickaxe:
  Id: minecraft:diamond_pickaxe
  Options:
    MyItemModel: amethyst:miners_pickaxe
    NameText: "<gold>Amethyst Pickaxe</gold>"
  Components:
    FlightMode: true
    FoodSettings:
      # Example of an advanced, pre-encoded Java 26.3 protocol payload:
      WireHex: "001122"
```

The `FoodSettings` example is **not a valid food payload**; it illustrates
syntax only. Do not use it on a live server. To remove any component
from the vanilla prototype, write `{ Remove: true }`. For unknown or
complex component formats, `WireHex` must contain the already encoded
*value bytes only*, not the component ID or overall Slot packet.

Aliases based on a vanilla material are still not distinct per-stack
identities: override the canonical vanilla item root to alter all stacks
of that material. Client-side custom models require the matching
resource pack.

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

| Registry ID | Component | YAML serialization |
| ---: | --- | --- |
| 0 | `minecraft:custom_data` | WireHex / Remove |
| 1 | `minecraft:max_stack_size` | Typed |
| 2 | `minecraft:max_damage` | Typed |
| 3 | `minecraft:damage` | Typed |
| 4 | `minecraft:unbreakable` | Typed |
| 5 | `minecraft:use_effects` | WireHex / Remove |
| 6 | `minecraft:custom_name` | Typed |
| 7 | `minecraft:minimum_attack_charge` | WireHex / Remove |
| 8 | `minecraft:damage_type` | WireHex / Remove |
| 9 | `minecraft:item_name` | Typed |
| 10 | `minecraft:item_model` | Typed |
| 11 | `minecraft:lore` | Typed |
| 12 | `minecraft:rarity` | Typed |
| 13 | `minecraft:enchantments` | WireHex / Remove |
| 14 | `minecraft:can_place_on` | WireHex / Remove |
| 15 | `minecraft:can_break` | WireHex / Remove |
| 16 | `minecraft:attribute_modifiers` | WireHex / Remove |
| 17 | `minecraft:custom_model_data` | Typed |
| 18 | `minecraft:tooltip_display` | Typed |
| 19 | `minecraft:repair_cost` | Typed |
| 20 | `minecraft:creative_slot_lock` | Typed |
| 21 | `minecraft:enchantment_glint_override` | Typed |
| 22 | `minecraft:intangible_projectile` | Typed |

### ID 23–45: use, equipment, and interactions

| Registry ID | Component | YAML serialization |
| ---: | --- | --- |
| 23 | `minecraft:food` | WireHex / Remove |
| 24 | `minecraft:consumable` | WireHex / Remove |
| 25 | `minecraft:use_remainder` | WireHex / Remove |
| 26 | `minecraft:use_cooldown` | WireHex / Remove |
| 27 | `minecraft:damage_resistant` | WireHex / Remove |
| 28 | `minecraft:tool` | WireHex / Remove |
| 29 | `minecraft:weapon` | WireHex / Remove |
| 30 | `minecraft:attack_range` | WireHex / Remove |
| 31 | `minecraft:enchantable` | Typed |
| 32 | `minecraft:equippable` | WireHex / Remove |
| 33 | `minecraft:repairable` | WireHex / Remove |
| 34 | `minecraft:glider` | Typed |
| 35 | `minecraft:tooltip_style` | Typed |
| 36 | `minecraft:death_protection` | WireHex / Remove |
| 37 | `minecraft:blocks_attacks` | WireHex / Remove |
| 38 | `minecraft:piercing_weapon` | WireHex / Remove |
| 39 | `minecraft:kinetic_weapon` | WireHex / Remove |
| 40 | `minecraft:attack_animation` | WireHex / Remove |
| 41 | `minecraft:interact_animation` | WireHex / Remove |
| 42 | `minecraft:additional_trade_cost` | WireHex / Remove |
| 43 | `minecraft:block_transformer` | WireHex / Remove |
| 44 | `minecraft:villager_food` | WireHex / Remove |
| 45 | `minecraft:stored_enchantments` | WireHex / Remove |

### ID 46–86: containers, stored data, and content

| Registry ID | Component | YAML serialization |
| ---: | --- | --- |
| 46 | `minecraft:dye` | WireHex / Remove |
| 47 | `minecraft:dyed_color` | Typed |
| 48 | `minecraft:map_id` | Typed |
| 49 | `minecraft:map_decorations` | WireHex / Remove |
| 50 | `minecraft:map_post_processing` | WireHex / Remove |
| 51 | `minecraft:charged_projectiles` | WireHex / Remove |
| 52 | `minecraft:bundle_contents` | WireHex / Remove |
| 53 | `minecraft:potion_contents` | WireHex / Remove |
| 54 | `minecraft:potion_duration_scale` | WireHex / Remove |
| 55 | `minecraft:suspicious_stew_effects` | WireHex / Remove |
| 56 | `minecraft:writable_book_content` | WireHex / Remove |
| 57 | `minecraft:written_book_content` | WireHex / Remove |
| 58 | `minecraft:trim` | WireHex / Remove |
| 59 | `minecraft:debug_stick_state` | WireHex / Remove |
| 60 | `minecraft:entity_data` | WireHex / Remove |
| 61 | `minecraft:bucket_entity_data` | WireHex / Remove |
| 62 | `minecraft:block_entity_data` | WireHex / Remove |
| 63 | `minecraft:instrument` | WireHex / Remove |
| 64 | `minecraft:provides_trim_material` | WireHex / Remove |
| 65 | `minecraft:ominous_bottle_amplifier` | WireHex / Remove |
| 66 | `minecraft:jukebox_playable` | WireHex / Remove |
| 67 | `minecraft:provides_banner_patterns` | WireHex / Remove |
| 68 | `minecraft:recipes` | WireHex / Remove |
| 69 | `minecraft:lodestone_tracker` | WireHex / Remove |
| 70 | `minecraft:firework_explosion` | WireHex / Remove |
| 71 | `minecraft:fireworks` | WireHex / Remove |
| 72 | `minecraft:profile` | WireHex / Remove |
| 73 | `minecraft:note_block_sound` | WireHex / Remove |
| 74 | `minecraft:banner_patterns` | WireHex / Remove |
| 75 | `minecraft:base_color` | WireHex / Remove |
| 76 | `minecraft:pot_decorations` | WireHex / Remove |
| 77 | `minecraft:container` | WireHex / Remove |
| 78 | `minecraft:block_state` | WireHex / Remove |
| 79 | `minecraft:bees` | WireHex / Remove |
| 80 | `minecraft:sulfur_cube_content` | WireHex / Remove |
| 81 | `minecraft:lock` | WireHex / Remove |
| 82 | `minecraft:container_loot` | WireHex / Remove |
| 83 | `minecraft:break_sound` | WireHex / Remove |
| 84 | `minecraft:compostable` | WireHex / Remove |
| 85 | `minecraft:cooking_fuel` | WireHex / Remove |
| 86 | `minecraft:brewing_fuel` | WireHex / Remove |

### ID 87–121: variants and remaining components

| Registry ID | Component | YAML serialization |
| ---: | --- | --- |
| 87 | `minecraft:mob_visibility` | WireHex / Remove |
| 88 | `minecraft:villager/variant` | WireHex / Remove |
| 89 | `minecraft:wolf/variant` | WireHex / Remove |
| 90 | `minecraft:wolf/sound_variant` | WireHex / Remove |
| 91 | `minecraft:wolf/collar` | WireHex / Remove |
| 92 | `minecraft:fox/variant` | WireHex / Remove |
| 93 | `minecraft:salmon/size` | WireHex / Remove |
| 94 | `minecraft:parrot/variant` | WireHex / Remove |
| 95 | `minecraft:tropical_fish/pattern` | WireHex / Remove |
| 96 | `minecraft:tropical_fish/base_color` | WireHex / Remove |
| 97 | `minecraft:tropical_fish/pattern_color` | WireHex / Remove |
| 98 | `minecraft:mooshroom/variant` | WireHex / Remove |
| 99 | `minecraft:rabbit/variant` | WireHex / Remove |
| 100 | `minecraft:pig/variant` | WireHex / Remove |
| 101 | `minecraft:pig/sound_variant` | WireHex / Remove |
| 102 | `minecraft:cow/variant` | WireHex / Remove |
| 103 | `minecraft:cow/sound_variant` | WireHex / Remove |
| 104 | `minecraft:chicken/variant` | WireHex / Remove |
| 105 | `minecraft:chicken/sound_variant` | WireHex / Remove |
| 106 | `minecraft:zombie_nautilus/variant` | WireHex / Remove |
| 107 | `minecraft:frog/variant` | WireHex / Remove |
| 108 | `minecraft:horse/variant` | WireHex / Remove |
| 109 | `minecraft:painting/variant` | WireHex / Remove |
| 110 | `minecraft:llama/variant` | WireHex / Remove |
| 111 | `minecraft:axolotl/variant` | WireHex / Remove |
| 112 | `minecraft:cat/variant` | WireHex / Remove |
| 113 | `minecraft:cat/sound_variant` | WireHex / Remove |
| 114 | `minecraft:cat/collar` | WireHex / Remove |
| 115 | `minecraft:sheep/color` | WireHex / Remove |
| 116 | `minecraft:shulker/color` | WireHex / Remove |
| 117 | `minecraft:provides_pottery_pattern` | WireHex / Remove |
| 118 | `minecraft:sign_text_front` | WireHex / Remove |
| 119 | `minecraft:sign_text_back` | WireHex / Remove |
| 120 | `minecraft:waxed` | Typed |
| 121 | `minecraft:cushion/color` | WireHex / Remove |

**Total: 122 configurable YAML nodes. 21 typed component codecs; every other registered component can use WireHex or Remove.**

Minecraft reference: [Data component format](https://minecraft.wiki/w/Data_component_format).
