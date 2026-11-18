"""No-SDK smoke checks. Runtime wire/role tests live in tests/ChatTests/."""
from pathlib import Path
import xml.etree.ElementTree as ET
import yaml
root=Path(__file__).resolve().parents[1]
cs=(root/'shards/Amethyst.Shards.Gameplay')
files=['MinecraftServer.cs','OperatorStore.cs','ChatProtocol.cs','CommandTree.cs','PlayerRegistry.cs']
for f in files:
    assert (cs/f).exists(),f
s=(cs/'MinecraftServer.cs').read_text()
protocol=(cs/'ChatProtocol.cs').read_text()
store=(cs/'OperatorStore.cs').read_text()
tree=(cs/'CommandTree.cs').read_text()
assert 'ChatProtocol.ParseChat(packet.Value)' in s
assert 'ChatProtocol.ParseCommand(packet.Value)' in s
assert 'ChatProtocol.ParseAck(packet.Value)' in s
assert 'ChatProtocol.ParseSessionUpdate(packet.Value)' in s
assert 'CommandTree.SendAsync' in s
assert 'Level(ConnectedPlayer player) => _options.OnlineMode ? _operators.Level(player.PlayerUuid) : 0' in s
assert 'target == player || Level(target) >= level' in s
assert 'GrantOperatorAsync' in s and 'RevokeOperatorAsync' in s
assert 'TryConsumeChatCooldown' in s
assert 'ClientboundSystemChat = 0x7C' in protocol
assert 'ServerboundChat = 0x09' in protocol
assert 'ServerboundCommand = 0x07' in protocol
assert 'ServerboundSignedCommand = 0x08' in protocol
assert 'ServerboundSessionUpdate = 0x0A' in protocol
assert 'private const int AckBitsetBytes = 3' in protocol
assert 'public static Task SendSystemAsync' in protocol
assert 'YamlDotNet' in store
assert 'Dictionary<Guid, OperatorEntry>' in store
assert 'TryGrant(Guid authenticatedUuid' in store
assert 'TryRevoke(' in store
assert 'p.WriteVarInt(5)' in tree and 'p.WriteVarInt(node.Greedy ? 2 : 0)' in tree
assert 'p.WriteVarInt(0); // root node index' in tree
settings=yaml.safe_load((root/'server.yaml').read_text())
assert settings['onlineMode'] and settings['operatorsFile']=='ops.yaml'
assert settings['chatEnabled'] and settings['chatMaxLength']==256
assert (root/'ops.example.yaml').exists() and not (root/'ops.yaml').exists()
assert yaml.safe_load((root/'ops.example.yaml').read_text()) == {'operators': []}
assert not list(root.glob('*.json'))
assert ET.parse(root/'src/Amethyst.csproj').getroot().find('.//TargetFramework').text=='net10.0'
assert ET.parse(root/'tests/ChatTests/ChatTests.csproj').getroot().find('.//TargetFramework').text=='net10.0'
assert 'operatorsFile: "ops.yaml"' in (root/'README.md').read_text()
print('PASS: chat packet IDs and parsers, operator identity/permission guards, YAML settings, command tree, test project')
