#!/usr/bin/env python3
"""No-SDK Phase 5 source/data guardrails; not a replacement for a .NET build."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import yaml

root = Path(__file__).resolve().parents[1]
settings = yaml.safe_load((root / 'server.yaml').read_text())
assert settings['generatorVersion'] == 7
assert settings['onlineMode'] is True
assert settings['saveGeneratedChunks'] is True
for name in ('featureDensity', 'oreFrequency', 'structureFrequency'):
    assert 0 <= settings[name] <= 2
assert settings['worldPath'] == 'worlds/phase7'
assert not list(root.rglob('*.json')), 'Configuration files must remain YAML'

state_source = (root / 'shards/Amethyst.Shards.Overworld/TerrainGenerator.cs').read_text()
expected = {
    'OakLog': 140, 'SpruceLog': 143, 'BirchLog': 146,
    'OakLeaves': 268, 'SpruceLeaves': 296, 'BirchLeaves': 324,
    'ShortGrass': 2363, 'Fern': 2364, 'Dandelion': 3813,
    'Cactus': 8599, 'CoalOre': 136, 'DeepslateCoalOre': 137,
    'IronOre': 134, 'DeepslateIronOre': 135, 'CopperOre': 31147,
    'DeepslateCopperOre': 31148, 'DiamondOre': 6863,
    'DeepslateDiamondOre': 6864, 'MossyCobblestone': 4924,
}
for name, value in expected.items():
    match = re.search(r'\b' + name + r'\s*=\s*(\d+)\b', state_source)
    assert match and int(match.group(1)) == value, f'{name}: unexpected 26.3 block state'
assert len(re.findall(r'public const ushort OakLog\s*=', state_source)) == 1
assert (root / 'shards/Amethyst.Shards.Overworld/Generation/Terrain/DensitySampler.cs').read_text().count('public int TopSolidAt(') == 1
for filename in ('FeatureHash', 'OreVeinGenerator', 'VegetationGenerator', 'StructureGenerator'):
    assert (root / 'shards/Amethyst.Shards.Overworld/Generation/Features' / (filename + '.cs')).exists()
assert (root / 'shards/Amethyst.Shards.Overworld/WorldStorage.cs').exists()
assert (root / 'src/ServerOptions.cs').exists()
project = ET.parse(root / 'src/Amethyst.csproj').getroot()
assert 'net10.0' in (root / 'src/Amethyst.csproj').read_text()
assert any(x.attrib.get('Remove') == 'FlatWorld.cs' for x in project.iter('Compile'))
manifest = (root / 'shards/Amethyst.Shards.Overworld/WorldStorage.cs').read_text()
assert 'world.yaml' in manifest and 'GZipStream' in manifest
assert 'GeneratorFingerprint' in manifest and 'SHA256.HashData' in manifest
assert 'FileMode.CreateNew' in manifest and 'File.Move(temp, destination)' in manifest
print('PASS: Phase 5 YAML, vanilla 26.3 state IDs, no duplicates, storage safety and feature sources')
