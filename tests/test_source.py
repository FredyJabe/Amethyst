#!/usr/bin/env python3
"""Portable invariant tests requiring no .NET SDK (not a substitute for compilation)."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
settings = (ROOT / "server.yaml").read_text()
assert not list(ROOT.rglob("*.json")), "All on-disk configuration files must use YAML"
assert "onlineMode: true" in settings
assert "viewDistance: 10" in settings
assert "seed: 87420913" in settings
assert "generatorVersion: 7" in settings
assert "aquifersEnabled: true" in settings
assert "aquiferLavaLevel: -48" in settings
assert "aquiferWaterTableOffset: 18" in settings
assert "surfaceDepth: 4" in settings
assert "featureDensity: 1.0" in settings
assert "oreFrequency: 1.0" in settings
assert "structureFrequency: 1.0" in settings
assert "saveGeneratedChunks: true" in settings
assert 'worldPath: "worlds/phase7"' in settings
assert "climateScale: 1.0" in settings
assert "baseHeight: 70" in settings
assert "heightVariation: 36" in settings
assert "biome: auto" in settings
assert "seaLevel: 63" in settings
assert "server.yaml" in (ROOT / "src/Program.cs").read_text()
assert "net10.0" in (ROOT / "src/Amethyst.csproj").read_text()
assert "YamlDotNet" in (ROOT / "src/Amethyst.csproj").read_text()
# Previous releases shipped FlatWorld.cs. Installing a ZIP over an old folder
# leaves that obsolete file on disk, so ensure it is excluded by MSBuild.
import xml.etree.ElementTree as ElementTree
project = ElementTree.parse(ROOT / "src/Amethyst.csproj").getroot()
assert any(item.attrib.get("Remove") == "FlatWorld.cs"
           for item in project.iter("Compile")), "exclude legacy FlatWorld.cs"

registries = dict(line.split("|", 1) for line in
                  (ROOT / "src/VanillaRegistries/registry_entries.txt").read_text().splitlines()
                  if line and not line.startswith("#"))
assert len(registries) == 32
assert "plains" in registries["minecraft:worldgen/biome"].split(",")

world = (ROOT / "shards/Amethyst.Shards.Overworld/TerrainWorld.cs").read_text()
assert "new ushort[4096]" in world, "Cuboid sections must carry 4096 block states"
assert "new int[64]" in world, "Biomes must have 4x4x4 samples"
assert "SectionCount = 24" in world
assert "BoundedLazyCache<ChunkPos, ChunkColumn>" in world
assert "WritePalette" in world and "BuildHeightmap" in world
assert "p.WriteVarInt(0); // Single-valued palettes" not in world
assert "p.WriteVarInt(wordCount)" not in world
assert "SerializeSections(ChunkPos pos)" in world
assert "p.WriteInt64(unchecked((long)word))" in world
assert "OnBatchReceivedAsync" in world
assert "_awaitingBatchAck" in world
lights = (ROOT / "shards/Amethyst.Shards.Overworld/ChunkLightEngine.cs").read_text()
assert "ChunkLightEngine.Calculate" in world
assert "_lights.GetOrAdd" in world
assert "SkyAt" in lights and "BlockAt" in lights
assert "SetSky" in lights and "SetBlock" in lights
assert "PlayerChunkView" in world and "SendColumnAsync" in world
assert "worldX is >= -2 and <= 2" in world
assert "GroundY" in world and "TopY" in world
assert "section.FluidCount" in world
terrain = (ROOT / "shards/Amethyst.Shards.Overworld/TerrainGenerator.cs").read_text()
assert "TerrainBlocks" in terrain and "Sample(int x, int z)" in terrain
assert "ValueNoise" in terrain and "Fbm" in terrain
assert "_seed" in terrain and "SurfaceKind" in terrain
assert "new ClimateSampler" in terrain and "new BiomeResolver" in terrain
assert "new TerrainShaper" in terrain and "LegacyHeight" in terrain
density = (ROOT / "shards/Amethyst.Shards.Overworld/Generation/Terrain/DensitySampler.cs").read_text()
assert "class DensitySampler" in density and "class DensityGrid" in density
assert "CellWidth = 4" in density and "CellHeight = 8" in density
assert "Interpolate" in density and "caveFade" in density
assert "GenerateDensity(pos)" in world and "PopulateBiomes(column, pos, terrainSamples)" in world
assert "new AquiferSampler(settings)" in world
assert "new MaterialRuleEngine(settings)" in world
assert "new CaveBiomeSampler(settings.Seed)" in world
assert "Flood(emitted, glow, opacity)" in lights
assert "MaterialRuleEngine.IsFluid(state)" in world
assert "_caveBiomes?.Resolve" in world
assert "AquiferLavaLevel" in (ROOT / "src/ServerOptions.cs").read_text()
assert (ROOT / "shards/Amethyst.Shards.Overworld/Generation/Terrain/AquiferSampler.cs").is_file()
assert (ROOT / "shards/Amethyst.Shards.Overworld/Generation/Terrain/MaterialRuleEngine.cs").is_file()
assert (ROOT / "shards/Amethyst.Shards.Overworld/Generation/Climate/CaveBiomeSampler.cs").is_file()
assert (ROOT / "tests/Phase4Tests/Program.cs").is_file()
assert (ROOT / "shards/Amethyst.Shards.Overworld/Generation/Noise/FractalNoise3D.cs").is_file()
assert (ROOT / "tests/DensityTests/Program.cs").is_file()
shape = (ROOT / "shards/Amethyst.Shards.Overworld/Generation/Terrain/TerrainShaper.cs").read_text()
for identifier in ("ContinentalCurve", "MountainRelief", "RiverInfluence", "PeaksAndValleys", "Erosion", "Interpolate"):
    assert identifier in shape, f"Missing Phase-2 terrain shaping feature: {identifier}"
assert "_biomeResolver.Resolve" in terrain
assert "SampleClimate" in terrain and "SampleClimate" in world
climate = ROOT / "shards/Amethyst.Shards.Overworld/Generation/Climate"
noise = ROOT / "shards/Amethyst.Shards.Overworld/Generation/Noise"
for filename in ("ClimateSample.cs", "ClimateSampler.cs", "BiomeResolver.cs"):
    assert (climate / filename).is_file(), f"Missing climate module: {filename}"
for filename in ("SeededGradientNoise.cs", "FractalNoise2D.cs"):
    assert (noise / filename).is_file(), f"Missing noise module: {filename}"
assert "GeneratorVersion" in (ROOT / "src/ServerOptions.cs").read_text()
assert "ClimateScale" in (ROOT / "src/ServerOptions.cs").read_text()
assert "MinecraftCfb8Cipher" in (ROOT / "src/PacketChannel.cs").read_text()

crypto = (ROOT / "src/OnlineAuthentication.cs").read_text()
for needle in ("hasJoined", "RSAEncryptionPadding.Pkcs1", "EnableEncryption", "SHA1.HashData",
               "FixedTimeEquals", "ExportSubjectPublicKeyInfo"):
    assert needle in crypto
cipher = (ROOT / "src/PacketChannel.cs").read_text()
cfb8 = (ROOT / "src/MinecraftCfb8Cipher.cs").read_text()
assert "CipherMode.ECB" in cfb8 and "TransformBlock(_feedback, 0, 16" in cfb8
assert "_feedback[15] = ciphertext" in cfb8
assert "new MinecraftCfb8Cipher(sharedSecret, encrypt: true)" in cipher
assert "new MinecraftCfb8Cipher(sharedSecret, encrypt: false)" in cipher
assert "SemaphoreSlim" in cipher
assert "_encrypt.Transform(buffer.Span" in cipher
assert "_decrypt.Transform(encrypted.AsSpan" in cipher

server = (ROOT / "shards/Amethyst.Shards.Gameplay/MinecraftServer.cs").read_text()
for needle in ("HandlePlayAsync", "BroadcastMovementAsync", "NotifyArrivalAsync",
               "NotifyDepartureAsync", "player.SetPose", "player.View.UpdateAsync"):
    assert needle in server
network = (ROOT / "src/AmethystNetworkServer.cs").read_text()
assert "AuthenticateAsync" in network and "OfflineUuid(username)" in network
assert "_options.OnlineMode" in network
assert "ProfileProperty" in (ROOT / "shards/Amethyst.Shards.Gameplay/PlayerPackets.cs").read_text() or "Properties" in (ROOT / "shards/Amethyst.Shards.Gameplay/PlayerPackets.cs").read_text()
assert not re.search(r'(?m)^\s*//.*dependency.free', (ROOT / "README.md").read_text(), flags=re.I)
assert "_ores?.Decorate(column)" in world
assert "_vegetation?.Decorate(column)" in world
assert "_structures?.Decorate(column)" in world
assert (ROOT / "shards/Amethyst.Shards.Overworld/Generation/Features/StructureGenerator.cs").is_file()
assert "BoundedLazyCache<ChunkPos, ChunkColumn>" in world
assert (ROOT / "shards/Amethyst.Shards.Overworld/WorldStorage.cs").is_file()
assert (ROOT / "shards/Amethyst.Shards.Overworld/Generation/Features/OreVeinGenerator.cs").is_file()
assert (ROOT / "shards/Amethyst.Shards.Overworld/Generation/Features/VegetationGenerator.cs").is_file()
assert (ROOT / "tests/Phase5Tests/Program.cs").is_file()
print("PASS: YAML configuration, versions 1–7, feature decoration/persistence, protocol, online auth and movement invariants")
