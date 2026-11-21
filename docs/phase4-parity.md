# Phase 4: Items, Enchantments, Entity Loot and Interactions

This is a feature-completion checklist for Amethyst's Java 26.3 / protocol 777
Items and Entities milestone. **Loading an official registry or loot table is
not the same as implementing the underlying gameplay.** Phase 4 remains
**incomplete** until all applicable acceptance conditions below pass.

## Implemented foundations

| Area | Current authoritative source and boundary |
| --- | --- |
| Item identity and metadata | Items YAML covers 1,658 vanilla protocol item IDs; only the user's 45 approved component YAML aliases are public, with 77 still pending explicit approval |
| Stats | Ordered item/enchantment `Stats` assignments; plain `- damage 50` means **set**, while `+10`, `-5`, `*1.5` change the preceding value |
| Enchantments | 43 entries with Core skills and stats; a subset of triggers is wired to gameplay |
| Vanilla loot | Pinned 1,447 Mojang JSON tables; audit reports 45 nested polymorphic variants, of which 43 are recognized, not necessarily fully executable for every context |
| Custom loot | Items YAML uses Core `~onDeath`, `~onShear` and `dropItem`; administrator overrides replace bundled packs, including `Skills: []` |
| One-shot deaths | Entities Shard is authoritative; melee, ranged and plugin deaths share the same one-shot dequeue and loot publication path |
| Entity XP | `Experience: 5` or `Experience: [1, 3]` in Entities YAML; player-attributed kills spawn physical persistent XP orbs and console kills do not |
| Entity persistence | Separate world-scoped stores for native sheep, generic YAML mobs (`AMGEN001`), ground items and XP; fishing hooks and projectiles are deliberately transient |
| World interaction | Validated sheep shearing, milk buckets, source water/lava buckets, tools, durability and physical dropped items |
| Equipment | Atomic, cancellable F-key swaps, accepted `~onEquip`/`~onUnequip` transitions, protocol-777 observer updates for main hand, offhand and armor |
| Consumption | Timed food/potion use, stack validation, bottles/bowls, healing/status effects, conservative chorus teleport and thrown splash-potion payloads |
| Combat | Player/managed-mob reach and damage, armor, shields, Resistance/Absorption, projectiles, gliding, equipment wear, and pre-impact `~onProjectileHit` skill dispatch |
| Interactions | Validated item `~onRightClick`, `~onMilk` and `~onShear` before state changes; `cancelEvent` leaves vanilla action uncommitted while executing later Skills |

## Known missing or incomplete capabilities

These are **not** silently declared supported:

1. **Mob lifecycles:** natural biome/light-based spawning, hostile AI,
   pathfinding, behavior goals, attacks, breeding and passengers, entity-specific
   physical collision, healing, and complete persistence of mob-specific data.
   A generic YAML mob with Health currently remains inert unless acted upon.
2. **Vanilla loot context:** authoritative block-entity state, environmental
   biome/weather data and advanced entity predicates; two observed, genuinely
   unsupported loot variants (`minecraft:exploration_map` in 18 source
   occurrences, `minecraft:dynamic` in one). **Recognition of 43 other
   variants is not proof that every JSON shape and condition is executable.**
3. **World-backed item systems:** complete map IDs/rendering/exploration maps,
   authored and writable books, decorated-pot sherd inventories, filled
   containers and the full block-entity inventory ecosystem. These need
   durable world services before they can be implemented faithfully.
4. **Specialized item interactions:** fish/axolotl/tadpole and powder-snow
   buckets, cauldrons, waterlogging/fluid physics, boats, minecarts, mounts,
   leads, saddles, name tags, armor stands and other mob-specific actions.
5. **Consumable and effect parity:** arbitrary per-stack `food` and
   `consumable` component overrides, full vanilla timing, lingering potions
   and area-effect clouds, complete effect-specific movement/visibility/
   environmental behavior, and all potion effects on non-player mobs.
6. **Enchantment parity:** weather-dependent Channeling, Frost Walker,
   Riptide, movement modifiers, all target-conditional damage types and
   armor/equipment attribute recalculation; generic Skills registration
   alone is not behavioral proof.
7. **Network/client behavior:** complete synchronized entity metadata and
   equipment for all species and actions, reliable teleport reconciliation,
   missing GUI/menu packets for specialized containers, and broad
   end-to-end testing with a real Java 26.3 client. A vanilla client locally
   predicts its own first-person swing, which cannot be undone by the server
   without a compatible client modification.
8. **Component naming policy:** the remaining 77 of 122 item-component YAML
   aliases need the user's explicit assignments; do not invent names or
   introduce a customizable node mapping file.

## Completion criteria

- An end-to-end integration test covers each implemented interaction's
  server-validated input, cancelled and accepted paths, durability/stock
  invariants, packet output, entity state and restart persistence.
- Server replays all 1,447 pinned loot tables over verified applicable
  contexts without silent or unintended fallbacks; map/dynamic and
  environment-dependent branches have their own world-authoritative services.
- All 43 vanilla enchantments have tested live behavior for every declared
  trigger/condition, including damage, movement, weather and XP paths.
- Remaining item components have explicitly approved YAML names and
  correct serializer + real behavior (rather than merely allowing `WireHex`).
- Generic mobs have complete, tested behavior and lifetime state rather
  than simply registry IDs, health values and display packets.
- Real Java 26.3 integration tests prove physical, visual and protocol
  parity. CI builds and the Shard/Entity/Block/Enchantment suites pass on
  the current PR head.

## Regression commands

```bash
git submodule update --init --recursive
dotnet build Amethyst.slnx -c Release
dotnet run --project tests/ShardTests/ShardTests.csproj -c Release --no-build
dotnet run --project tests/EntityTests/EntityTests.csproj -c Release --no-build
dotnet run --project tests/EnchantmentTests/EnchantmentTests.csproj -c Release --no-build
dotnet run --project tests/BlockTests/BlockTests.csproj -c Release --no-build
python3 tests/audit_vanilla_loot.py
```

PR #21 remains Draft because **vanilla-complete Phase 4 is not yet achieved**.
The existing improvements are usable and tested, but none of the missing
features above should be presented as complete.
