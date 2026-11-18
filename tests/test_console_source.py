"""No-SDK checks for the terminal console integration and YAML configuration."""
from pathlib import Path
import xml.etree.ElementTree as ET
import yaml

root=Path(__file__).resolve().parents[1]
server=(root/'shards/Amethyst.Shards.Gameplay/MinecraftServer.cs').read_text()
program=(root/'src/Program.cs').read_text()
terminal=(root/'src/ServerConsole.cs').read_text()
editor=(root/'src/ConsoleLineEditor.cs').read_text()
completions=(root/'shards/Amethyst.Shards.Gameplay/ConsoleCommandCompletion.cs').read_text()
settings=yaml.safe_load((root/'server.yaml').read_text())
assert settings['consoleMode']=='auto'
assert settings['consoleHistorySize']==100
assert 'new ServerConsole(options.ConsoleMode, options.ConsoleHistorySize)' in program
assert 'Console.CancelKeyPress += OnCancel;' in program
assert 'Console.CancelKeyPress -= OnCancel;' in program
assert 'terminal.StartInputAsync(' in (root/'src/AmethystNetworkServer.cs').read_text()
assert 'ExecuteConsoleCommandAsync(' in server
assert 'case "kick":' in server and 'case "op":' in server
assert 'case "deop":' in server and 'case "say":' in server
assert 'Channel.CreateBounded<ServerLogMessage>' in terminal
assert 'Console.SetOut(_stdout)' in terminal and 'Console.SetError(_stderr)' in terminal
assert 'Console.ReadKey(intercept: true)' in terminal
assert '_originalIn.ReadLineAsync(stop)' in terminal
assert 'DrawPromptUnsafe()' in terminal
assert 'ReadAllAsync()' in terminal
assert 'ConsoleKey.UpArrow' in editor and 'ConsoleKey.DownArrow' in editor
assert 'ConsoleKey.Tab' in editor and 'ConsoleKey.Backspace' in editor
assert '"op" or "kick" => onlinePlayers' in completions
assert not (root/'src/FlatWorld.cs').exists()
assert ET.parse(root/'tests/ConsoleTests/ConsoleTests.csproj').getroot().find('.//TargetFramework').text=='net10.0'
print('PASS: console integration, typed editing, completion and YAML settings')
