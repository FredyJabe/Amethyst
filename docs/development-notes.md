# Amethyst — Original Overworld, chat, OP permissions and sparse aquifers (generator v7)

Experimental **C# / .NET 10** Minecraft **Java Edition 26.3 (protocol 777)** server. It implements multiplayer server-list presence, online Mojang login, Configuration/Play transition, player movement/rotation synchronization, and a deterministic original (vanilla-*inspired*) Overworld.

**This is a prototype**, not a production-ready Minecraft replacement. The source archive includes the required generator files and test projects; no external map or vanilla `level.dat` is required.

## Interactive server console (new)

All existing `Console.WriteLine` and `Console.Error.WriteLine` messages are now
funneled through one bounded, thread-safe log queue. In a real terminal the
prompt remains visible even when authentication, chat, player activity or chunk
logs arrive while you are typing. Entries include timestamps and categories:
INFO, AUTH, CHAT, DEBUG, WARNING and ERROR.

**Keyboard controls:** Up/Down browse recent commands; Left/Right, Home/End,
Backspace/Delete edit the current line; Tab completes console commands or
connected player names (`op`, `deop`, `kick`); Ctrl+A/Ctrl+E move to the start/end;
Ctrl+U removes the text before the cursor. The input scrolls horizontally when
longer than the terminal. Only fully submitted commands are executed. Ctrl+C
requests a graceful shutdown rather than abruptly killing the world server.

Server console commands:

```text
help                 Show server console command help
list                 List online players
oplist               Show operators and their levels
op <player> [1-4]    Grant OP permissions (online authenticated player)
deop <name|uuid>     Revoke OP permissions
kick <player> [reason]  Disconnect an online player
say <message>        Broadcast a server message
clear                Clear the interactive terminal output
stop                 Save and stop the server
```

Configuration remains YAML:

```yaml
consoleMode: auto       # auto, interactive, plain
consoleHistorySize: 100 # history size 1-1000; in-memory only
```

`auto` uses the live prompt only when stdin and stdout are attached to a
terminal that supports ANSI cursor controls. When running under systemd, a
pipe, a log collector, or an IDE that redirects output, it **automatically
falls back** to plain lines with no colors or cursor-control sequences;
commands are still accepted from stdin if available. Set `consoleMode: plain`
to explicitly disable interactive rendering. `NO_COLOR=1` disables ANSI
colors while retaining the live editable prompt. `interactive` requests the
live prompt but falls back safely if the terminal cannot support it. Console
history is **not stored on disk**, and no command strings are saved in YAML.

To start from a terminal:

```bash
./run.sh
# or
dotnet run --project src/Amethyst.csproj -- server.yaml
```

For regression checks:

```bash
dotnet run --project tests/ConsoleTests/ConsoleTests.csproj
```

The console uses .NET's `System.Threading.Channels` and standard terminal APIs;
no additional NuGet package is required. Output backpressure drops excess
messages if over 8,192 logs accumulate, with a warning once output resumes.

## New in the visual-world update

**Day/night clock (Minecraft Java 26.3 / protocol 777):** The server now sends
`set_time` (`0x73`) using the current world-clock packet: world age, an entry
for the `minecraft:overworld` clock, a VarLong clock time, fractional time and
rate. It synchronizes all online players every five seconds; the client
interpolates the sun, moon and sky brightness between packets. By default,
24,000 game ticks take 20 real-world minutes. The server uses a monotonic
clock instead of depending on the operating system's wall-clock adjustments.

```yaml
startTimeTicks: 1000   # 0 sunrise, 6000 noon, 12000 sunset, 18000 midnight
dayLengthMinutes: 20  # 2–120; 20 matches normal Java Minecraft
daylightCycle: true   # false freezes the visible time of day
```

For persisted worlds (`generatorVersion` 5/6/7 with `saveGeneratedChunks: true`),
`worldPath/time.yaml` remembers world time and world age. The server autosaves
once a minute and again on graceful shutdown. This YAML file is generated
**state**, not a config to edit while the server runs. In nonpersistent worlds,
time starts from `startTimeTicks` after each restart. You can change day
length without changing your saved world's generation fingerprint.

**Light engine:** Each generated column now has full 16×16×16 nibble-array
skylight and blocklight data. Direct sky exposure filters through leaves and
water, sunlight spreads into side openings, and nearby lava spreads warm
blocklight. A **15-block halo** reads neighboring chunk columns so light does
not stop abruptly on a chunk border. Light packets follow the protocol-777
26-section sky/block masks and are cached for immutable generated chunks.

Sky light represents *maximum natural light*, **not daylight at the current
hour**. The client applies sunrise, sunset and nighttime dimming using the
world-clock packet. This avoids retransmitting all lighting every second.

This is still a simple static light engine, not vanilla's fully incremental
lighting simulation: it does not recalculate after block edits, flow fluids,
implement emissive non-lava blocks or simulate weather. It uses simple
opacity rules for the blocks our generator currently places.

**Generator version 6** adds distinct, seeded tree silhouettes (irregular oak
crowns, narrow birch, layered conifers, tall branching jungle trees, leaning
umbrella acacias, broad dark oaks, mangroves), patches of flowers and tall
grass, and occasional fallen logs, varied boulders and weathered ruins.
All feature anchors use absolute coordinates and are clipped per column.
Version **5 still generates its older features unchanged**, and earlier
versions 1–4 retain their prior terrain mapping.

### Important: existing saved worlds

Decoration changes the generated blocks. Do **not** change a persisted version-5
world to `generatorVersion: 6` while pointing at the same `worldPath`; the world
manifest rejects mismatched generator settings to prevent corrupt/mixed world
geometry. The included `server.yaml` uses `worldPath: "worlds/phase6"` for a
**new world**. Keep your old version-5 world directory as a backup. To keep an
existing version-5 landscape, retain `generatorVersion: 5` and its original
`worldPath`: **day/night and improved lighting work on version 5 too**, while
new tree shapes apply only to newly generated version-6 chunks.

### Test it

```bash
dotnet run --project tests/VisualTests/VisualTests.csproj
dotnet run --project tests/ChunkTests/ChunkTests.csproj
dotnet run --project tests/Phase5Tests/Phase5Tests.csproj
dotnet run --project src/Amethyst.csproj -- server.yaml
```

The visual test covers the 26.3 world-clock packet, clock-rate calculation,
static lava and sky light, shaded foliage, cross-chunk illumination, seven
species-specific tree shapes, and version-6 world/time persistence. **The .NET
build and real-client render must still be verified on a machine with .NET 10
and Minecraft installed.**

## Chunk visibility fix (Minecraft Java 26.3)

This hotfix addresses invisible-but-collidable sections while moving around the
world. **No generation rules or saved-chunk formats have changed.** Existing
Phase 5 `worldPath` data can be reused without regenerating terrain.

- **Correct unloading coordinates:** `ClientboundForgetLevelChunkPacket` (`0x26`)
  carries **Z before X** (two 32-bit integers). The previous server sent X/Z,
  so diagonal travel could forget the wrong visible chunk while the server
  continued treating it as loaded.
- **Unload before updating client cache center:** vanilla clients may ignore
  unload packets if the target is already outside their new chunk cache.
- **Send initial player position before chunks:** prevents loading the first
  section meshes for the wrong pre-teleport camera location.
- **One extra ring of chunks:** keeps the neighboring columns available for
  chunk mesh building. The server's advertised `viewDistance` remains unchanged.
- **Optional chunk-visibility tracing:** start with
  `AMETHYST_TRACE_CHUNKS=1 dotnet run --project src/Amethyst.csproj -- server.yaml`
  to see center changes, unload counts, and chunk batches.

Run `dotnet run --project tests/ChunkTests/ChunkTests.csproj` to exercise
coordinate ordering, negative-coordinate movement, batch acknowledgements,
and spawn packet ordering. `python3 tests/test_chunk_streaming.py` runs a
limited source-level check without .NET.

If gaps remain, try `F3+A` to rebuild chunk meshes, then test the vanilla
client with rendering modifications disabled and change the 26.3 **Graphics API**
(OpenGL ↔ Vulkan). A client renderer bug can show the same symptoms, especially
if the client is modded; those are not always fixable by server packets.

## Phase 5 additions (`generatorVersion: 5`)

- **Trees** — oak, birch, spruce, jungle, acacia, dark-oak and mangrove-style trees selected by biome. Seeded 16×16 feature anchors produce both trunks and canopies. Woodland and taiga are denser than plains. Some styles use simplified canopies and cherry groves currently reuse birch wood.
- **Ground vegetation** — short grass, ferns, common flowers, mushrooms and desert cacti. Placement avoids the spawn platform, water, and invalid surface blocks.
- **Ore veins** — coal, iron, copper, gold, redstone, lapis, diamond and emerald, with separate stone/deepslate states and altitude-dependent distribution. Small multi-block veins form independently of chunk order.
- **Small landmarks** — occasional surface boulders and weathered stone/sandstone arches. These are basic decoration features, **not** villages, temples, mineshafts or dungeons.
- **Seamless feature placement** — all feature candidates are determined in absolute coordinates; chunks generate the local portion of a neighboring tree/vein/landmark, with no recursive neighbor generation. Positive and negative chunk coordinates use floor division correctly.
- **Generated-chunk persistence** — the server saves each newly generated v5 chunk as a compressed `.mc26` file under `worldPath/chunks/`, and loads it on subsequent server runs. `worldPath/world.yaml` stores the seed, generator version and generation-settings fingerprint. A mismatch fails fast rather than mixing different worlds.
- **Updated metadata** — non-air/fluid block counts, 4×4×4 biome palettes and heightmaps reflect the decorated chunks. The existing 26.3 palette and chunk-batch protocol remains unchanged.

All new block states were checked against the Minecraft Java 26.3 block registry, including log axis and persistent leaves: [RosegoldMC Minecraft data](https://github.com/RosegoldMC/minecraft-data.cr/blob/master/data/26.3/blocks.json).

## Example `server.yaml` (generator version 7)

```yaml
bindAddress: "127.0.0.1"
port: 25565
motd: "Amethyst | Procedural World"
maxPlayers: 20
onlineMode: true
viewDistance: 10
startTimeTicks: 1000
dayLengthMinutes: 20
daylightCycle: true

seed: 87420913
generatorVersion: 7
climateScale: 1.0
baseHeight: 70
seaLevel: 63
heightVariation: 36
terrainScale: 1.0
biome: auto

aquifersEnabled: true
aquiferLavaLevel: -48
aquiferWaterTableOffset: 18
surfaceDepth: 4

featureDensity: 1.0
oreFrequency: 1.0
structureFrequency: 1.0
saveGeneratedChunks: true
worldPath: "worlds/phase7"
```

The three frequency controls accept **0..2** (`0` disables that feature family). `worldPath` is relative to the directory where the server is started unless an absolute path is supplied. The seed is a signed 64-bit integer. **Keep `onlineMode: true`** when hosting outside your own computer.

`generatorVersion` preserves compatibility with older algorithms:

| Version | Behavior |
|---|---|
| `1` | Original procedural heightmap |
| `2` | Continental terrain shaping and climate regions |
| `3` | 3D density, dry caves, overhangs |
| `4` | Adds aquifers, cave biomes and material rules |
| `5` | Adds Phase 5 decoration and optional saved chunks |
| `6` | Natural tree shapes and clustered features; same base terrain plus persistent clock |
| `7` | Sparse aquifer regions, mostly dry caves; retains v6 terrain and decoration |

Versions 1–4 retain their previous terrain behavior and do **not** use saved-chunk files. Feature settings apply to v5 and later; naturalized decoration applies to v6 and v7. Changing a seed, generator version or generation parameter in an **existing persisted world** requires a new `worldPath` (or intentional world reset). Changing `viewDistance`, port, or MOTD doesn't change the world fingerprint.

### Persisted worlds and backup safety

- `world/world.yaml` is a YAML **manifest**, not a second general server configuration.
- `world/chunks/<x>.<z>.mc26` files are GZip-compressed **binary chunk data**, not configuration files. They contain all 24 block sections and their biome samples.
- New chunks are written via temporary files and renamed into place after successful completion. A corrupt chunk causes an error and is not silently overwritten.
- World snapshots currently persist **generated terrain only**. Block editing/placing, fluid simulation and gameplay state changes are not yet implemented, so there is no live block-edit persistence, inventory persistence or player data storage.
- A chunk already loaded in memory is reused for the life of the server. World writes happen during initial generation; a future release should add chunk dirty tracking, backups and region-based storage.
- Set `saveGeneratedChunks: false` to regenerate all chunks in memory on every start (useful while testing generator changes).

For initial performance testing, a **viewDistance of 4–6** is more conservative than 10: Phase 5 samples density and features while generating many new chunks. Once chunks are saved, repeat visits avoid regeneration.

## Install and run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), Minecraft Java Edition 26.3 and Internet access for Mojang session authentication. The project uses YamlDotNet 18.1.0.

**Extract into a clean directory.** Earlier ZIPs shipped `FlatWorld.cs`, which conflicts with `TerrainWorld.cs` if left under `src`. The `.csproj` excludes the old file, but clean extraction is still the safest upgrade.

```bash
dotnet restore src/Amethyst.csproj
dotnet run --project src/Amethyst.csproj -- server.yaml
```

To trace network packets:

```bash
AMETHYST_TRACE_PACKETS=1 dotnet run --project src/Amethyst.csproj -- server.yaml
```

If you start with a previously generated world, **do not simply set `generatorVersion: 5` while keeping the same persistent world path**. Use a fresh `worldPath`, such as `"worlds/phase5-seed87420913"`, and retain a backup of your old directory.

## Tests

```bash
dotnet run --project tests/Phase5Tests/Phase5Tests.csproj
dotnet run --project tests/Phase4Tests/Phase4Tests.csproj
dotnet run --project tests/DensityTests/DensityTests.csproj
dotnet run --project tests/ChunkTests/ChunkTests.csproj
python3 tests/test_source.py
python3 tests/test_chunk_packet.py
python3 tests/test_phase5_assets.py
python3 tests/test_chunk_streaming.py
```

The Phase 5 tests cover YAML parsing, feature determinism and chunk-generation order, ore/vegetation presence, section counts, heightmaps, backward-compatibility when features are disabled, persisted chunk reload, invalid world-settings rejection, corrupt-file handling and the protected 5×5 stone spawn platform (Y=79).

The project **has not been tested end-to-end with a Minecraft client** in the packaging environment, which lacks the .NET SDK and the game. The `.NET` regression suites must be run on a machine with .NET 10.

## Remaining limitations

The existing skylight algorithm is a simplified vertical approximation. It does not calculate vanilla skylight/blocklight diffusion across chunk boundaries; dense tree canopies may therefore look too dark. The generator doesn't yet add vanilla-quality tree variations, biomes' full feature sets, villages, structures with loot, entities, cave decoration, ore distribution exact to vanilla, server-side physics, chunk editing, or world tick simulation. Feature palettes are vanilla 26.3 block IDs, but the **seed-to-world mapping is entirely Amethyst's own**.


## Chat and operator permissions (Phase: Chat & OP)

Minecraft Java 26.3 chat is enabled through protocol-777 client chat packets,
server-side command parsing, and `ClientboundSystemChat` (NBT text components).
Messages are **not cryptographically verified/signed** by this prototype; they
appear as server/system messages attributed to Mojang-authenticated usernames.
The server advertises `enforcesSecureChat: false`. Do not mistake an online-mode
session check for signed-chat integrity or moderation/reporting support.

Use `server.yaml` for chat settings and **`ops.yaml`** for persistent privileges (automatically created on first run; `ops.example.yaml` is only a template).
Both are YAML, not JSON. The generated world has its own `world.yaml` manifest;
compressed chunk snapshots remain binary.

```yaml
onlineMode: true
chatEnabled: true
chatMaxLength: 256
chatCooldownMilliseconds: 350
operatorsFile: "ops.yaml"
```

Operator privileges are stored by **authenticated Mojang UUID**, not usernames.
Names in `ops.yaml` are display labels only. With `onlineMode: false`, **all
connected players have permission level 0** (even UUIDs appearing in `ops.yaml`).
The server rejects in-game OP grants without online authentication.

### Granting the first operator

1. Start the server in an interactive terminal: `dotnet run --project src/Amethyst.csproj -- server.yaml`.
2. Join the server with a legitimate authenticated Java Edition account.
3. In the **server console**, enter `op PlayerName 4` (replace `PlayerName` with
   the name shown in the `[auth]` / `[ready]` logs). You must be fully connected.
4. `ops.yaml` is saved automatically with the verified UUID. The client receives
   the new command tree immediately; no restart required.

The console also accepts `deop PlayerName`, `list`, `say Hello world`, and
`stop`. You can also stop the server with `Ctrl+C`. You can also edit `ops.yaml` **while the server
is stopped** to add an operator using a known verified UUID.

### In-game commands

| Command | Minimum level | Description |
|---|---:|---|
| `/help`, `/list`, `/whoami` | 0 | Help, online players, account UUID and permission level |
| `/msg <player> <text>` (`/tell`, `/w`) | 0 | Private message |
| `/me <action>` | 0 | Action message |
| `/gamemode <survival|creative|adventure|spectator> [online player]` | 1 | Change your own or another connected player's game mode |
| `/say <text>` | 2 | Broadcast server announcement |
| `/kick <player> [reason]` | 2 | Disconnect a lower-level player |
| `/oplist` | 2 | Show current operators |
| `/op <online player> [1-4]` | 4 | Grant/change an authenticated player's level |
| `/deop <name|uuid>` | 4 | Revoke OP privileges, including stored offline entries |

Every command is re-authorized server-side; client command visibility alone
never grants privileges. Operator levels range from 1 through 4. Level 1 unlocks
`/gamemode`, level 2 unlocks moderation, and level 4 unlocks OP management.
Level 3 is reserved for future features. An OP cannot kick an equal/higher-level OP.

Example: give an authenticated player permission with `op ExamplePlayer 1` in the
server console (or `/op ExamplePlayer 1` as a level-4 operator). Then the player can
run `/gamemode survival`, `/gamemode creative`, `/gamemode adventure`,
`/gamemode spectator`, or `/gamemode creative OtherPlayer`. Target players must
be connected. Changes are synchronized to the target's client abilities and
all players' tab lists. They are **session-only** for now: on reconnect, players
start in the server's existing default Creative mode. This changes client game
mode behavior but does not add survival damage, inventory or block-interaction
logic (those systems are not implemented yet). The server also recognizes
26.3's F3+F4 switch request and applies the same OP-level check; whether the
shortcut is available in the client can depend on vanilla client permissions.

Run the game-mode packet and role tests with:

```bash
dotnet run --project tests/GameModeTests/GameModeTests.csproj
dotnet run --project tests/ChatTests/ChatTests.csproj
```

The current world generator, saved chunks, `server.yaml`, and `ops.yaml` do not
need migration for this feature.

The client packet handler supports plain command packets, signed command
envelopes without signed arguments, chat acknowledgements, and session updates.
Commands containing signed arguments are currently rejected rather than
processed without signature verification. The command tree contains the
supported commands and updates when permissions change.

**Limitations:** No signed player-chat verification, per-recipient chat reporting,
chat history, mute/ban commands, channel routing, command completion beyond the
advertised Brigadier tree, or chat persistence. Messages are rate-limited and
newline/control-character injection is rejected. Chat settings changes require
restarting the server.

### Tests

```bash
dotnet run --project tests/ChatTests/ChatTests.csproj
dotnet run --project tests/ChunkTests/ChunkTests.csproj
```


## v7: game-mode switcher and dry cave aquifers

- OP level 1 or higher can open **F3+F4** to switch modes. Hold F3,
  tap F4 and release F3 to select. When a player enters Play or their OP
  status changes, the server sends `entity_event` permission information.
  The vanilla 26.3 client requires level 2 to show this UI, so OP 1 is
  reported as level 2 **to that client only**. The actual operator level
  remains 1 in `ops.yaml` and is enforced by the server on every command.
  Some other vanilla level-2 UI controls may appear enabled but are not
  authorized on the server.
- Typing `/gamemode` with no argument gives F3+F4 instructions. Vanilla
  has no server-to-client packet that remotely opens this local screen.
- Generator version **7** uses sparse 3D aquifer regions so that most
  caves remain dry; selected deeper pockets contain water or lava.
  Versions 4–6 retain their old deterministic aquifer mapping.
- Version 7 defaults to `worldPath: "worlds/phase7"` to keep existing
  saved chunks intact. Previously generated version-6 chunks will retain
  their old water until deliberately regenerated in a separate world.

### Testing

```bash
dotnet run --project tests/GameModeTests/GameModeTests.csproj
dotnet run --project tests/AquiferTests/AquiferTests.csproj
dotnet run --project src/Amethyst.csproj -- server.yaml
```

## Chunk performance update

See [PERFORMANCE.md](PERFORMANCE.md) for the new bounded generation workers,
light-input and packet caches, background save queue, benchmark commands and
safe tuning guidelines. The live interactive console accepts `perf`.
