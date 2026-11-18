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


## Creative block editing (experimental)

Creative-mode players can break blocks and place a **curated set of 168 simple
vanilla blocks** using the Creative inventory and hotbar. This release introduces
`IShardBlockWorld` as an optional capability registered by the Overworld Shard,
without adding gameplay responsibilities to Core or changing the existing
`IShardWorld` ABI. World Shards may provide their own implementation.

Player actions are range-checked, restricted to Creative mode, rate-limited
(64 edits/s/player), synchronized with `block_update`, and acknowledged using
the client's prediction sequence ID. Supported placed blocks are a version-locked
subset without complex block properties or block entities. Creative hotbar items
with data-component patches are not yet supported; a general inventory will
require a separate parser and persistence model.

**Edits storage:** generated `.mc26` snapshots remain immutable. When
`saveGeneratedChunks: true` and `generatorVersion` is 5–7, sparse edits are
atomically saved in `worldPath/edits/<chunkX>.<chunkZ>.amedit`. Back up both
`chunks/` and `edits/` together. If persistence is disabled (or generator
version is 1–4), edits are session-only. Corrupt sidecar files fail loudly rather
than being silently overwritten. Frame and lighting caches are revisioned so
future chunk loads incorporate edits.

**Limitations:** no Survival mining progression, drops, inventory consumption,
multiblock placement, block physics, fluid updates or neighboring block behaviors.
Existing clients receive block-state changes immediately; full dynamic relighting
of their already-loaded chunks remains follow-up work.

To run the automated regression tests:

```bash
dotnet build Amethyst.slnx
dotnet run --project tests/BlockTests/BlockTests.csproj
```
