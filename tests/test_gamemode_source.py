"""Source-level regression checks. Live packet semantics are covered in GameModeTests (.NET 10)."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
src = root / 'shards/Amethyst.Shards.Gameplay'
mode = (src / 'GameModeProtocol.cs').read_text()
server = (src / 'MinecraftServer.cs').read_text()
commands = (src / 'CommandTree.cs').read_text()
players = (src / 'PlayerRegistry.cs').read_text()
spawn = (src / 'SpawnPackets.cs').read_text()
tab = (src / 'PlayerPackets.cs').read_text()

assert 'public const int RequiredOpLevel = 1' in mode
for word in ('Survival = 0', 'Creative = 1', 'Adventure = 2', 'Spectator = 3'):
    assert word in mode
assert 'ClientboundGameEvent = 0x27' in mode
assert 'ClientboundPlayerAbilities = 0x41' in mode
assert 'ClientboundPlayerInfoUpdate = 0x47' in mode
assert 'ServerboundChangeGameMode = 0x05' in mode
assert 'UpdateGameModeAction = 0x04' in mode
assert 'p.WriteUuid(playerId)' in mode
assert 'SendLocalChangeAsync' in mode
assert 'SendTabUpdateAsync' in mode
assert 'CanChange(Level(player))' in server
assert '"gamemode" => GameModeProtocol.RequiredOpLevel' in server
assert 'case "gamemode":' in server
assert 'GameModeProtocol.TryParseArguments' in server
assert 'ChangeGameModeAsync(target, mode, player, ct)' in server
assert 'target.SetGameMode(mode)' in server
assert 'GameModeProtocol.SendTabUpdateAsync(observer.Connection, target.PlayerUuid' in server
assert '("gamemode", 1, true)' in commands
assert 'public PlayerGameMode GameMode' in players
assert 'p.WriteVarInt((int)player.GameMode)' in spawn
assert 'GameModeProtocol.SendAbilitiesAsync' in spawn
assert 'p.WriteVarInt((int)subject.GameMode)' in tab
assert (root / 'tests/GameModeTests/Program.cs').is_file()
assert ET.parse(root / 'tests/GameModeTests/GameModeTests.csproj').getroot().find('.//TargetFramework').text == 'net10.0'
assert not (root / 'ops.yaml').exists(), 'Must not ship operator credentials'
print('PASS: gamemode OP1, client/peer synchronization, switch request, tests and YAML operator safety')
