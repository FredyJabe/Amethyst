#!/usr/bin/env python3
"""Source and YAML guardrails when .NET 10 SDK is unavailable.

These checks are NOT a replacement for `dotnet build` and VisualTests.
"""
from pathlib import Path
import xml.etree.ElementTree as ET
import yaml

root = Path(__file__).resolve().parents[1]
settings = yaml.safe_load((root / 'server.yaml').read_text())
assert settings['generatorVersion'] == 7
assert settings['worldPath'] == 'worlds/phase7'
assert settings['startTimeTicks'] == 1000
assert settings['dayLengthMinutes'] == 20
assert settings['daylightCycle'] is True
assert settings['onlineMode'] is True
assert not list(root.rglob('*.json')), 'Config must remain YAML only'

clock = (root / 'shards/Amethyst.Shards.Gameplay/WorldClock.cs').read_text()
server = (root / 'shards/Amethyst.Shards.Gameplay/MinecraftServer.cs').read_text()
spawn = (root / 'shards/Amethyst.Shards.Gameplay/SpawnPackets.cs').read_text()
writer = (root / 'src/Protocol.cs').read_text()
light = (root / 'shards/Amethyst.Shards.Overworld/ChunkLightEngine.cs').read_text()
world = (root / 'shards/Amethyst.Shards.Overworld/TerrainWorld.cs').read_text()
trees = (root / 'shards/Amethyst.Shards.Overworld/Generation/Features/NaturalTreeShapes.cs').read_text()
plants = (root / 'shards/Amethyst.Shards.Overworld/Generation/Features/VegetationGenerator.cs').read_text()
landmarks = (root / 'shards/Amethyst.Shards.Overworld/Generation/Features/StructureGenerator.cs').read_text()
for value in ('Stopwatch', 'WriteVarLong(whole)', 'WriteVarInt(1)',
              'minecraft:world_clock', 'dayLengthMinutes', 'time.yaml', 'File.Move'):
    assert value in (clock + (root / 'src/ServerOptions.cs').read_text()), value
assert 'SendAsync(0x73, WritePacket' in clock
assert 'WriteVarLong(long number)' in writer
assert 'await clock.SendAsync(channel, ct)' in spawn
assert 'BroadcastWorldTimeAsync' in server and '_clock.Save()' in server
for name in ('Opacity', 'Flood', 'LightData', 'Halo = 15', 'FullSky',
             'WriteMask', 'SkyAt', 'BlockAt', 'TerrainBlocks.OakLeaves',
             'TerrainBlocks.Lava'):
    assert name in light, name
assert 'ChunkLightEngine.Calculate(this, key)' in world
assert 'lighting.Write(writer)' in world
assert 'WriteLightData(p, column)' not in world
for name in ('Conifer(', 'Acacia(', 'DarkOak(', 'Jungle(', 'Mangrove(',
             'Broadleaf(', 'Blob(', 'LeafDisk('):
    assert name in trees, name
assert 'generatorVersion' in (root / 'src/ServerOptions.cs').read_text()
assert 'settings.GeneratorVersion >= 6' in plants
assert 'settings.GeneratorVersion >= 6' in landmarks
assert 'PatchValue' in plants and 'DecorateNatural' in landmarks
assert (root / 'tests/VisualTests/Program.cs').is_file()
assert ET.parse(root / 'tests/VisualTests/VisualTests.csproj').getroot().tag == 'Project'
print('PASS: YAML-only clock settings, protocol-777 time/light writer, v6 natural features and visual tests')
