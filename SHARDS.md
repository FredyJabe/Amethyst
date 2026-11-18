# Amethyst Shards — plugin API v1

Amethyst is now organized into a **network kernel** and independently compiled **Shards**. Shards are trusted .NET 10 assemblies; they execute with the same permissions as the server process. Install only assemblies you trust.

## Architecture

| Project | Responsibility |
| --- | --- |
| `src/Amethyst.csproj` | TCP listener, status/ping, protocol framing, Mojang authentication, encryption, login/configuration, connection lifetime, Shard loader, operator console host |
| `shards/Amethyst.Shards.Overworld` | Terrain/biomes, chunks, lighting, save files, chunk preparation and streaming |
| `shards/Amethyst.Shards.Gameplay` | Play-state packets, players, movement, game modes, permissions, chat, day/night clock |
| `shards/Amethyst.Shards.Hello` | Disabled-by-default example: a `hello` console command and a Play packet observer |

Core deliberately does **not** reference either built-in Shard assembly. The protocol API (e.g., `PacketChannel`, `PacketReader`, `PacketWriter`) and Shard contracts (`Amethyst.Shards`) are available from the core project. Existing generator versions **1–7** and the `.mc26` chunk save format are unchanged.

## Build / run

```bash
dotnet build Amethyst.slnx
# Builds and installs each Shard as shards/installed/<id>/{shard.yaml,*.dll}
dotnet run --no-build --project src/Amethyst.csproj -- server.yaml
```

On Linux, `./run.sh` does both steps automatically. **Do not run only `dotnet run --project src/Amethyst.csproj` on a fresh checkout**: it builds only the network kernel, not the Shard assemblies.

`shards/installed/` and `shards/data/` are ignored by Git. Configure their paths through `shardsDirectory` and `shardsDataDirectory` in `server.yaml`. The build targets always copy Shard DLLs, but copy each manifest only when it is missing: local `enabled` edits survive rebuilds. Shard-specific writable data belongs in `shards/data/<shard-id>/`; saves and operators remain at their existing paths.

## Shard manifest

Each installed Shard lives in a directory containing a `shard.yaml` and its DLL:

```yaml
id: overworld
apiVersion: 1
assembly: Amethyst.Shards.Overworld.dll
entryType: Amethyst.BuiltinShards.OverworldShard
enabled: true
provides:
  - world
requires: []
dependsOn: []
```

Manifest fields:

- `id`: unique lowercase identifier (`[a-z][a-z0-9-]{1,48}`)
- `apiVersion`: currently **1**
- `assembly`: DLL filename in the manifest directory
- `entryType`: fully qualified class name implementing `IShard` with a public parameterless constructor
- `enabled`: whether to enable automatically on startup
- `provides`: optional named capabilities such as `world`
- `requires`: capabilities that must have exactly one enabled provider
- `dependsOn`: explicit Shard IDs (for non-substitutable dependencies)

A dependency cycle, missing required provider, duplicate ID, incompatible API version, missing DLL, or invalid manifest causes a clear startup error. The Gameplay Shard requires the `world` capability rather than referring directly to `overworld`; an alternative provider can be substituted **without changing the gameplay binary**.

To replace the built-in world provider, set `enabled: false` on the installed `overworld/shard.yaml`, install a trusted Shard that declares `provides: [world]`, and set `enabled: true` on that Shard. Its enable hook must register one `IShardWorld` service. Keep the same world save only if the new provider is actually save-format and generator compatible; otherwise choose a new `worldPath`.

## Lifecycle

1. **Discover / instantiate:** the host loads each manifest and DLL, then calls `OnLoadAsync(IShardContext, ct)`.
2. **Enable:** dependencies and capabilities are resolved first, then `OnEnableAsync(ct)` is called. Register services, packet handlers, and console commands here.
3. **Disable:** dependents must be disabled first; `OnDisableAsync(ct)` runs, then the host removes that Shard's service, packet, and command registrations.
4. **Shutdown:** enabled Shards stop in reverse dependency order. Gameplay closes before the world provider, ensuring pending saves can drain safely.

Each Shard has its own noncollectible assembly load context and resolves private dependencies from its installation directory (include its dependency DLLs and `.deps.json` when distributing third-party Shards). The shared Amethyst API assembly is never loaded twice.

**Enable/disable does not unload .NET assemblies.** DLL replacement or code updates require a process restart. The Gameplay Shard refuses to disable while players are connected. World providers cannot disable while an enabled Gameplay Shard depends on them.

Console commands:

```text
shards
shard enable hello
hello World
shard disable hello
shard disable gameplay
shard disable overworld
shard enable gameplay   # automatically enables a world provider
```

Other commands (`op`, `deop`, `kick`, `say`, `perf`) continue to come from the Gameplay Shard. `stop` is owned by Core.

## Public API

Reference `src/Amethyst.csproj` from your Shard project (target `net10.0`). Implement `Amethyst.Shards.IShard`:

```csharp
using Amethyst.Shards;

public sealed class MyShard : IShard
{
    private IShardContext? _context;

    public Task OnLoadAsync(IShardContext context, CancellationToken ct)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task OnEnableAsync(CancellationToken ct)
    {
        var ctx = _context!;
        ctx.RegisterConsoleCommand("example", (arguments, token) =>
        {
            ctx.Log($"Command received: {arguments}");
            return Task.CompletedTask;
        });
        ctx.RegisterPlayPacketHandler(0x2C, (player, packet, token) =>
        {
            ctx.Log($"Player loaded: {player.Username}");
            return Task.FromResult(false); // false: don't consume packet
        });
        return Task.CompletedTask;
    }

    public Task OnDisableAsync(CancellationToken ct) => Task.CompletedTask;
}
```

The `IShardContext` exposes a writable `DataDirectory`, typed service registration/retrieval, logging, Play packet handlers and server-console command registration. Shards can call `GetRequiredService<IShardWorld>()` to access the active world provider, or implement `IShardWorld` themselves. `IShardWorld` exposes dimension/seed/spawn metadata, ground-height queries, and per-player `IShardChunkView` factories. The `IShardPacketBus` dispatches Play packet hooks in registration order before the Gameplay Shard's default handler; returning `true` consumes the packet.

The public API is **experimental** and will evolve. Do not rely on internal engine types, private implementation details, or runtime assembly unloading. Shards have full filesystem and network access like any other in-process .NET code; there is no sandbox.

## Compatibility and migration

`server.yaml` still contains the existing world/gameplay YAML settings for now to preserve existing users and generator fingerprints. The classes that consume them have moved to their respective Shards; the network kernel does not generate terrain. Per-Shard configuration migration can happen separately without changing seed-to-world behavior.

Existing `ops.yaml`, world directories, v7 seeds, and `.mc26` chunk files are unchanged. For upgrading from a pre-Shards build, **use a clean source checkout** and copy over `server.yaml`, `ops.yaml` and your saved world directories. Avoid extracting over a modified tree with stray source files; `Amethyst.csproj` excludes known legacy source locations, but a clean checkout is still safer.

The Minecraft protocol's server-list status uses JSON on the wire because the vanilla client requires it. All project configuration files remain YAML.


## Block interaction (experimental)

### Creative and Survival

Both **Creative** and **Survival** support breaking blocks, placing supported
blocks, and synchronizing changes to other players viewing the same chunk.
The initial mode for each login is controlled by `defaultGameMode` in
`server.yaml`: `creative` (backward-compatible default), `survival`,
`adventure` or `spectator`. In-game `/gamemode` overrides last until
disconnect, then the configured default applies on the next login.
**Adventure** and **Spectator** cannot modify the world. Gameplay uses the
optional `IShardBlockWorld` capability; Core still handles networking only.

Creative breaks blocks instantly and places items without consuming them.
**Java 26.3 digging action IDs:** `0=START`, `1=CHANGE_DESTROY_DIRECTION`,
`2=ABORT`, `3=STOP`. This is **not** the pre-26.3 enum (the new
direction action shifts ABORT/STOP and item drop statuses). A genuine
26.3 STOP at status 3 must commit the world edit and publish the
authoritative air state; interpreting it as an item drop causes client-side
broken blocks to reappear the next time the server sends updates.

Survival tracks start/cancel/finish dig actions per player, checks block hardness
and selected tool type/tier against Java Edition 26.3 data, and validates
elapsed time before accepting a break. STOP is accepted at 70% of the expected
mining duration, allowing for the vanilla client's tick-based completion and
minor packet scheduling skew. It also commits **one-tick instant breaks
directly on START**: vanilla does not send a STOP packet for blocks mined in
one tick. For longer digs, Java 26.3's ordered `client_tick_end` (0x0D)
markers also count elapsed digging time. This prevents queued START and STOP
packets being mistaken for a zero-duration dig when synchronous world work
delays processing them. Tick markers are a compatibility aid while Amethyst
lacks a production tick scheduler; they are not hardened anti-cheat evidence.
The server sends confirmed block updates to the breaker **before**
acknowledging prediction sequences to prevent previously mined blocks from
reappearing when the next action arrives.

If a client still sees ghost blocks, enable action-level diagnostics:

```bash
AMETHYST_TRACE_MINING=1 ./run.sh
```

For each dig the console prints the received START/CANCEL/STOP, position,
selected item, server block state, whether the edit was committed, and a
specific rejection reason. A committed block reports `changed=True`,
`after=0`; an uncommitted block shows why (missing START, target mismatch,
too early, unbreakable, out of range, or edit throttled). This is an
opt-in diagnostic flag, not a general verbose-packet capture. The current mining model is
*approximate*: it does not yet model enchantments, haste, mining fatigue,
underwater penalties, status effects, tool durability, or all vanilla exceptions.

The first Survival loot implementation collects a **conservative subset of
simple drops directly into player inventory**. Items do not yet spawn as
physical entities. Unsupported loot tables, silk touch, Fortune, stack drops,
and full-block entity contents remain future work. If the inventory is full,
unsupported extra drops are not spawned. Do not expect vanilla-equivalent
Survival progression yet.

Creative can place its palette of simple blocks, plus axis-aware wooden logs
and persistent leaves. Survival can place those blocks only if present in its
selected hotbar stack; placing consumes one item. Multi-block structures,
interactable blocks, containers, furniture, slab/stair shape selection, fluids,
falling block physics and redstone behavior remain unsupported.

### Inventory

Gameplay owns authoritative 36-slot main inventory + hotbar data (menu slots
9–44), selected hotbar index, and cursor. Basic pickup/right-click, shift-click
and number-key swaps are handled server-side; client changed-slot hashes are
not trusted. Armor, crafting, offhand behavior, drop entities, drag distribution,
and component-bearing/custom items are not yet implemented. Unsupported clicks
are reconciled with the server's contents. Player inventory and cursor data are
persisted atomically under `shards/data/gameplay/inventories/<uuid>.bin` (or
the configured `shardsDataDirectory`). Back up this directory with the world.

### World edits and networking

Generated `.mc26` snapshots remain unchanged. For generator versions 5–7
with `saveGeneratedChunks: true`, sparse edits are atomically stored in
`worldPath/edits/<chunkX>.<chunkZ>.amedit`; for older/nonpersistent worlds,
edits are in memory only. Back up `chunks/` and `edits/` together.

Player edits have a basic six-block reach check and a 64 edits/second budget.
The server sends authoritative block updates and prediction acknowledgements
(sequence IDs) in protocol 777 and refreshes cache versions for newly streamed
chunks. **Already-loaded chunks do not yet receive full dynamic lighting
recalculations**; the new lighting is reflected when chunks are streamed again.

### Tests

```bash
dotnet build Amethyst.slnx -c Release
dotnet run --project tests/BlockTests/BlockTests.csproj -c Release --no-build
dotnet run --project tests/ShardTests/ShardTests.csproj -c Release --no-build
```

Manual acceptance: start with a backed-up world, connect two Java 26.3 clients,
change into Survival using `/gamemode survival`, mine dirt by hand until a
stack is collected, place it from the hotbar, and watch updates on both
clients. Switch to Creative to test logs and instant breaks. Restart to verify
world edits and inventory persistence.
