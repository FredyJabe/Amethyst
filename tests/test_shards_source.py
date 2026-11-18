"""No-SDK architecture and manifest checks (not a C# build or runtime test)."""
from pathlib import Path
import xml.etree.ElementTree as ET
import yaml

root=Path(__file__).resolve().parents[1]
core=root/'src'
world=root/'shards/Amethyst.Shards.Overworld'
game=root/'shards/Amethyst.Shards.Gameplay'
assert (root/'Amethyst.slnx').exists()
# Source-only overlays can leave old files physically present, but the Core
# project must exclude them from compilation.
project=(core/'Amethyst.csproj').read_text()
for filename in ('TerrainWorld.cs','TerrainGenerator.cs','MinecraftServer.cs','WorldClock.cs','PlayerRegistry.cs'):
    assert filename in project, f'{filename} must be excluded from the network core'
network=(core/'AmethystNetworkServer.cs').read_text()
assert 'HandleLoginAsync' in network and 'HandleConfigurationAsync' in network
assert 'IShardGameplay' in network and 'new TerrainWorld' not in network
worldcode=(world/'TerrainWorld.cs').read_text()
assert 'IShardWorld' in worldcode and 'IShardChunkView' in worldcode
assert 'GetCacheSummary()' in worldcode
assert 'GetRequiredService<IShardWorld>()' in (game/'GameplayShard.cs').read_text()
api=(core/'Shards/IShard.cs').read_text()
for method in ('OnLoadAsync','OnEnableAsync','OnDisableAsync',
               'RegisterConsoleCommand','RegisterPlayPacketHandler','IShardWorld',
               'IShardGameplay','IShardPacketBus'):
    assert method in api, method
loader=(core/'Shards/ShardHost.cs').read_text()
for needle in ('ShardLoadContext','CheckCycles(',
               'FindProvider(', 'EnableAsync(', 'DisableAsync(', 'DisposeAsync(',
               'Unregister(', 'RegisterPacket(', 'RegisterConsole('):
    assert needle in loader, needle
for project in (world,game,root/'shards/Amethyst.Shards.Hello'):
    files=list(project.glob('*.csproj'))
    assert len(files)==1 and ET.parse(files[0]).find('.//TargetFramework').text=='net10.0'
    manifest=yaml.safe_load((project/'shard.yaml').read_text())
    assert manifest['apiVersion']==1
    assert manifest['assembly'].endswith('.dll')
    assert isinstance(manifest['dependsOn'],list)
    assert isinstance(manifest['provides'],list)
    assert isinstance(manifest['requires'],list)
assert yaml.safe_load((game/'shard.yaml').read_text())['requires']==['world']
assert yaml.safe_load((world/'shard.yaml').read_text())['provides']==['world']
assert not yaml.safe_load((root/'shards/Amethyst.Shards.Hello/shard.yaml').read_text())['enabled']
assert (root/'tests/ShardTests/Program.cs').exists()
assert 'dotnet build Amethyst.slnx' in (root/'run.sh').read_text()
assert not any(root.rglob('*.json')), 'Configuration must remain YAML'
print('PASS: Core/Shard separation, YAML manifests, capabilities, lifecycle API and example plugin')
