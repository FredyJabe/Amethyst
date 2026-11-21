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
SUPPORTED = {
    "minecraft:item", "minecraft:empty", "minecraft:loot_table",
    "minecraft:alternatives", "minecraft:group", "minecraft:sequence",
    "minecraft:tool/can_silk_touch", "minecraft:tool/can_shear",
    "minecraft:survives_explosion", "minecraft:inverted",
    "minecraft:any_of", "minecraft:all_of", "minecraft:random_chance",
    "minecraft:table_bonus", "minecraft:killed_by_player",
    "minecraft:entity_properties", "minecraft:block_state_property",
    "minecraft:location_check", "minecraft:set_count",
    "minecraft:enchanted_count_increase", "minecraft:apply_bonus",
    "minecraft:explosion_decay", "minecraft:limit_count",
    "minecraft:furnace_smelt", "minecraft:set_damage",
    "minecraft:set_potion", "minecraft:constant", "minecraft:uniform",
    "minecraft:binomial",
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
        def walk(value):
            if isinstance(value, dict):
                kind = value.get("type")
                if isinstance(kind, str) and kind.startswith("minecraft:") and kind != table_type:
                    observed[kind] += 1
                    examples.setdefault(kind, str(path.relative_to(DIRECTORY)))
                for v in value.values():
                    walk(v)
            elif isinstance(value, list):
                for v in value:
                    walk(v)
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
    # Source completeness is enforced; unsupported complex behavior is
    # reported rather than disguised as full parity.

if __name__ == "__main__":
    scan()
