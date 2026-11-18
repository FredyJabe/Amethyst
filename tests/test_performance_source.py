"""Portable static/invariant checks for the chunk performance release.
These are not a C# compile or client test: use PerformanceTests for runtime validation.
"""
from pathlib import Path
import xml.etree.ElementTree as ET
import yaml

root=Path(__file__).resolve().parents[1]
options=yaml.safe_load((root/'server.yaml').read_text())
assert options['generatorVersion']==7 and options['worldPath']=='worlds/phase7'
assert options['chunkWorkers']>=1 and options['maxQueuedChunks']>=options['chunkWorkers']
for key in ['maxCachedColumns','maxCachedLights','maxCachedPackets','cachedTerrainSamples',
            'cachedFeatureHeights','saveQueueCapacity']:
    assert options[key]>=1
assert options['asyncWorldSaves'] is True
assert not list(root.rglob('*.json'))
world=(root/'shards/Amethyst.Shards.Overworld/TerrainWorld.cs').read_text()
server=(root/'shards/Amethyst.Shards.Gameplay/MinecraftServer.cs').read_text()
light=(root/'shards/Amethyst.Shards.Overworld/ChunkLightEngine.cs').read_text()
scene=(root/'shards/Amethyst.Shards.Overworld/Generation/Terrain/DensitySampler.cs').read_text()
store=(root/'shards/Amethyst.Shards.Overworld/WorldStorage.cs').read_text()
scheduler=(root/'shards/Amethyst.Shards.Overworld/ChunkPreparationScheduler.cs').read_text()
metrics=(root/'shards/Amethyst.Shards.Overworld/ChunkPerformance.cs').read_text()
assert 'BoundedLazyCache<ChunkPos, ChunkColumn>' in world
assert 'BoundedLazyCache<ChunkPos, ChunkLightEngine.LightData>' in world
assert 'BoundedLazyCache<ChunkPos, byte[]>' in world
assert 'PrepareChunkFrame' in world and 'channel.WriteAsync(frames[i], batchToken)' in world
assert 'byte[] cellSigns = grid.ClassifyCells()' in world
assert 'grid.Interpolate(x, y, z)' in world
assert 'ClassifyCells()' in scene
assert 'ArrayPool<byte>.Shared.Rent(total)' in light
assert 'FullyOpaqueAndUnlit' in light
assert 'QueueSave(generated)' in world and 'ProcessSavesAsync' in store
assert 'WorldManifest' in store and 'ComputeFingerprint' in store
assert 'CreateBounded<ChunkColumn>' in store
assert 'PriorityQueue<Job' in scheduler and 'SemaphoreSlim _slots' in scheduler
assert 'job.Subscribers' in scheduler and 'TaskCompletionSource<byte[]>' in scheduler
assert 'QueueWait' in metrics and 'GC.GetTotalMemory' in metrics
assert 'case "perf":' in server
assert 'await _world.DisposeAsync();' in (root/'shards/Amethyst.Shards.Overworld/OverworldShard.cs').read_text()
project=ET.parse(root/'tests/PerformanceTests/PerformanceTests.csproj')
assert project.getroot().find('.//TargetFramework').text=='net10.0'
# Every world setting used in the persistent generator fingerprint is unchanged.
for setting in ['s.Seed','s.GeneratorVersion','s.ClimateScale','s.BaseHeight',
                's.SeaLevel','s.FeatureDensity','s.OreFrequency','s.StructureFrequency']:
    assert setting in store
print('PASS: bounded scheduler/cache, persistent saves, pooled lighting, v7 identity and profiling hooks')
