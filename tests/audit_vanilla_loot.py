#!/usr/bin/env python3
"""Audit the exact Java 26.3 loot types supplied by the pinned Mojang extractor.

This is not a parity claim. It intentionally distinguishes 1,447 source tables
from the subset of condition / modifier / number-provider variants currently
handled by the C# evaluator, so missing behavior stays visible in CI.
"""
import collections
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DIRECTORY = ROOT / "vendor" / "minecraft-26.3-data" / "data" / "minecraft" / "loot_table"
# Recognition is checked independently from context completeness. These
# modifier/predicate types have executable C# handlers; some handlers still
# require world-owned state or have intentionally strict unsupported shapes.
SUPPORTED = {
    "minecraft:item", "minecraft:empty", "minecraft:loot_table",
    "minecraft:alternatives", "minecraft:group", "minecraft:sequence",
    "minecraft:tag", "minecraft:tool/can_silk_touch",
    "minecraft:tool/can_shear", "minecraft:survives_explosion",
    "minecraft:inverted", "minecraft:any_of", "minecraft:all_of",
    "minecraft:random_chance", "minecraft:table_bonus",
    "minecraft:killed_by_player", "minecraft:entity_properties",
    "minecraft:block_state_property", "minecraft:location_check",
    "minecraft:match_block", "minecraft:match_tool",
    "minecraft:damage_source_properties",
    "minecraft:random_chance_with_enchanted_bonus",
    "minecraft:set_count", "minecraft:enchanted_count_increase",
    "minecraft:apply_bonus", "minecraft:enchant_randomly",
    "minecraft:enchant_with_levels", "minecraft:copy_components",
    "minecraft:set_enchantments", "minecraft:set_components",
    "minecraft:set_stew_effect", "minecraft:set_ominous_bottle_amplifier",
    "minecraft:copy_state", "minecraft:explosion_decay",
    "minecraft:limit_count", "minecraft:furnace_smelt",
    "minecraft:set_damage", "minecraft:set_potion",
    "minecraft:set_name", "minecraft:set_instrument",
    "minecraft:filtered", "minecraft:discard",
    "minecraft:constant", "minecraft:uniform",
    "minecraft:binomial", "minecraft:linear",
}
# Complex features need server-owned data and do not yet have a safe
# implementation. Keep explicit to avoid claiming complete parity.
KNOWN_UNIMPLEMENTED = {
    "minecraft:exploration_map",
    "minecraft:dynamic",
}

def scan():
    files = sorted(DIRECTORY.rglob("*.json"))
    if len(files) != 1447:
        raise RuntimeError(f"Expected 1,447 official tables, got {len(files)}: {DIRECTORY}")
    observed = collections.Counter()
    examples = {}
    for path in files:
        table = json.loads(path.read_text(encoding="utf8"))
        table_type = table.get("type", "")
        if not isinstance(table.get("pools", []), list) or not table_type.startswith("minecraft:"):
            raise RuntimeError(f"Invalid vanilla loot table: {path}")
        def walk(value, field=None):
            if isinstance(value, dict):
                kind = value.get("type")
                # set_stew_effect uses type for a MOB EFFECT ID, not a
                # loot interpreter polymorphic type.
                effect_value = field == "effects" and "duration" in value
                if (isinstance(kind, str) and kind.startswith("minecraft:")
                        and kind != table_type and not effect_value):
                    observed[kind] += 1
                    examples.setdefault(kind, str(path.relative_to(DIRECTORY)))
                for key, v in value.items():
                    walk(v, key)
            elif isinstance(value, list):
                for v in value:
                    walk(v, field)
            elif isinstance(value, str) and value.startswith("minecraft:tool/"):
                observed[value] += 1
                examples.setdefault(value, str(path.relative_to(DIRECTORY)))
        walk(table)
    unknown = {k: n for k, n in observed.items() if k not in SUPPORTED}
    print(f"LOOT AUDIT: {len(files)} source tables, {len(observed)} total "
          f"nested type variants, {len(unknown)} not recognized by the current evaluator.")
    print("LOOT AUDIT: All observed types (recognition does not imply full semantic support):")
    for key, count in sorted(observed.items(), key=lambda item: (-item[1], item[0])):
        print(f"  {'UNKNOWN' if key in unknown else 'recognized':11} "
              f"{count:6d} {key} (e.g. {examples[key]})")
    unexpected = set(unknown) - KNOWN_UNIMPLEMENTED
    if unexpected:
        raise RuntimeError(f"Unreviewed native loot types: {sorted(unexpected)}")
    # Recognition is NOT a claim of full gameplay parity. Missing world/map
    # services and the exact subvariant semantics remain independently audited.

if __name__ == "__main__":
    scan()
