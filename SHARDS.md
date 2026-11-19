# Amethyst Shards — plugin API v1

Amethyst is now organized into a **network kernel** and independently compiled **Shards**. Shards are trusted .NET 10 assemblies; they execute with the same permissions as the server process. Install only assemblies you trust.

## Architecture

| Project | Responsibility |
| --- | --- |
| `src/Amethyst.csproj` | Network kernel, login/configuration, Shard loader, operator console, **global skill parser/registry/dispatcher**, and Shard extension contracts |
| `shards/Amethyst.Shards.Overworld` | Terrain/biomes, chunks, lighting, save files, chunk preparation and streaming |
| `shards/Amethyst.Shards.Items` | YAML-based item definitions, recipes, and triggered item skills (`dropItem` on `~onDeath`) for Java 26.3 |
| `shards/Amethyst.Shards.Entities` | YAML-configured registry of 161 vanilla Java 26.3 entity types and in-memory runtime for spawned entities |
| `shards/Amethyst.Shards.Gameplay` | Play-state packets, players, movement, game modes, inventory, crafting stations, chat, day/night clock; dispatches Core skills and provides world-item effect sink |
| `shards/Amethyst.Shards.Hello` | Disabled-by-default example: a `hello` console command and a Play packet observer |

Core deliberately does **not** reference any built-in Shard assembly. The protocol API (e.g., `PacketChannel`, `PacketReader`, `PacketWriter`) and Shard contracts (`Amethyst.Shards`) are available from the core project. Existing generator versions **1–7** and the `.mc26` chunk save format are unchanged.

## Items Shard (item definition ownership)

The built-in `items` capability supplies
`Amethyst.Shards.IShardItemRegistry`: an immutable Java 26.3 catalog of
**1,658 vanilla items** (protocol IDs 0–1657), namespaced names,
maximum stack sizes (1, 16, or 64), and block-item flags.

Gameplay looks up these definitions to validate Creative inventory edits,
item pickups, persisted stacks, and furnace/crafting slot transactions. For
example, swords and pickaxes can no longer stack to 64; an ender pearl can
stack to 16. The Items Shard also owns the **recipe definitions and matching**;
Gameplay owns crafting windows, ingredient consumption, furnace ticks, block
placement, mining and entity physics. Core additionally hosts the generic skill orchestration framework, but item-specific effects remain in Shards.

A Shard that consumes item definitions declares `requires: [items]`
and resolves the service in `OnEnableAsync`:

```csharp
IShardItemRegistry items = context.GetRequiredService<IShardItemRegistry>();
if (items.TryGetByIdentifier("diamond_pickaxe", out ShardItemDefinition definition))
    context.Log($"Protocol item {definition.ProtocolId}; max stack {definition.MaxStackSize}");
```

Inspect definitions in the server console with `iteminfo diamond_pickaxe`,
`iteminfo minecraft:crafting_table`, or `iteminfo 405`.

**All item definitions are YAML-configurable.** The pinned vanilla
catalog is `shards/Amethyst.Shards.Items/items-26.3.yml`, copied to the
installed Items Shard during build. Place MythicMobs-style override files in
`shards/data/items/definitions/*.yml` or `*.yaml` (respecting the configured
`shardsDataDirectory`), each with item IDs as YAML root keys. For example:

```yaml
diamond_pickaxe:
  Id: minecraft:diamond_pickaxe
  Display: "A customized pickaxe"
  Lore:
    - "Server-defined metadata"
  MaxStackSize: 1
  Options:
    BlockItem: false

LegendaryPickaxe:
  Id: diamond_pickaxe
  Display: "An internal alias of the pickaxe"
```

Vanilla overrides affect the server-owned stack validation immediately after
restart. Display, ItemName, Lore and CustomModelData are currently
**definition metadata only**, not yet serialized to client item components.
Alias roots can be looked up by name but are not distinct per-stack custom
items until Gameplay supports item identity/components. Unknown YAML
properties and illegal protocol ID changes are rejected at startup.

The same Items Shard also provides the `recipes` capability as
`IShardRecipeRegistry`. Recipes are defined using one top-level recipe ID per
root YAML node. Bundled defaults live in `recipes-26.3.yml`; put custom
crafting, smelting, smoking and blasting recipes in
`shards/data/items/recipes/*.yml`. For example:

```yaml
quick_torches:
  Type: Shapeless
  Ingredients: [minecraft:coal, minecraft:stick]
  Result: { Id: minecraft:torch, Amount: 8 }

smelt_stone_faster:
  Type: Furnace
  Ingredient: minecraft:cobblestone
  CookingTime: 100
  Result: { Id: minecraft:stone, Amount: 1 }
```

Gameplay requires both `items` and `recipes`, so its workstations consume
the provider's definitions rather than a hard-coded recipe table.
Administrators can override an existing recipe by repeating its root ID,
or disable it with `Enabled: false`; unsupported names and malformed
recipes are rejected on startup. The server console `recipes` command
lists recipe IDs. Recipe-book packet synchronization is not implemented yet.

See `shards/Amethyst.Shards.Items/README.md` for complete YAML keys,
recipe formats, precedence rules, version provenance and examples. Vanilla protocol IDs
cannot be replaced with arbitrary modded item IDs without client registry,
asset and protocol support.

## Entities Shard (entity type and instance ownership)

The built-in `entities` capability exposes `IShardEntityRegistry` (161 Java
26.3 / protocol 777 entity types) and `IShardEntityManager` (spawn, lookup,
movement, optional configured health/damage and removal). Both live outside
the network kernel. Gameplay uses the manager's shared entity ID allocator
for player and dropped-item network IDs and replicates registered instances
to nearby clients with loaded chunks.

The full immutable client registry is bundled at
`shards/Amethyst.Shards.Entities/entities-26.3.yml`. Configure types with
root-keyed `*.yml` or `*.yaml` files in
`shards/data/entities/definitions/` (or the configured shard data directory).
For example:

```yaml
cow:
  Health: 30
  Display: "Farm Cow"

guardian_cow:
  Id: minecraft:cow
  Health: 50
  Options:
    Invulnerable: true
    Spawnable: true
```

Vanilla IDs and hitbox sizes cannot be changed by YAML, because Java clients
know the base entity registry. An alias points to a vanilla type but retains
its own server-side definition. The supported configurable fields are
`Display`, `Health`, `Spawnable` and `Invulnerable`, with the last three also
accepted under `Options`. `Display` is definition metadata and is not
yet rendered as a custom name. `Health` affects manager-driven damage, not
vanilla combat packets; types without configured health currently have no
health/damage model. Item, player and other special protocol entity types are
non-spawnable by default.

Commands in the **server terminal**: `entityinfo cow`,
`entityspawn cow 0 80 0`, `entities`, and `entityremove <runtime-id>`.
Entity positions are static unless another system calls `TryTeleport`.
There is no autonomous AI, mob pathfinding, natural spawning, automated
drops, mob persistence, or complete combat yet. Existing dropped item pickup
and physics remain in Gameplay; spawned entity instances are transient.

See [the Entities Shard guide](shards/Amethyst.Shards.Entities/README.md)
for the exact YAML schema, C# service API, examples and limitations.

## Core skill engine (shared by all Shards)

The global `Amethyst.Shards.SkillEngine` runs **inside Amethyst Core**.
Every Shard gets the same `IShardSkillEngine` through
`context.GetRequiredService<IShardSkillEngine>()`.
Core owns skill YAML parsing, validation, registry merging, event
dispatch and cleanup on Shard disable. It does **not** know how to
spawn items, damage entities or perform other gameplay mechanics.

Shards can contribute independent mechanics, event triggers and
YAML packs through `IShardContext`:

```csharp
context.RegisterSkillTrigger("onInteract"); // once, by the event provider
context.RegisterSkillMechanic("myAction", ValidateMyAction, ExecuteMyAction);
context.RegisterSkillDefinitions(bundledYamlPath, customYamlDirectory);

// Anywhere with access to the Core skill engine:
IShardSkillEngine engine = context.GetRequiredService<IShardSkillEngine>();
await engine.DispatchAsync(new ShardSkillEvent(
    "minecraft:cow", "onInteract", 2, 70, 3), ct);
```

`ValidateMyAction(ShardSkillInstruction skill)` checks action
parameters at load time; `ExecuteMyAction(ShardSkillInstruction skill,
ShardSkillEvent evt, CancellationToken ct)` returns `ValueTask`.
A Shard may register its own mechanic and can also use mechanics
registered by other enabled Shards. Each Shard's user YAML overrides
its bundled YAML; different Shards' skill lists for the same root
**combine**, rather than overwrite one another. An explicit
`Skills: []` suppresses vanilla fallback for a custom alias.
Core manages registrations by Shard ID and removes the owning
Shard's mechanics, triggers, and definitions during disable.

`onDeath` is a built-in trigger in Core's initial trigger registry.
Other triggers can be registered by Shards that emit them.
Unregistered mechanics/triggers and invalid YAML fail validation.

### Items `dropItem` mechanic on `~onDeath`

The Items Shard contributes the `dropItem` action, validates item
IDs/chances/counts, and owns starter definitions in
`skills-26.3.yml` with overrides at
`shards/data/items/skills/*.yml` / `*.yaml`:

```yaml
cow:
  Skills:
    - 'dropItem{item=minecraft:beef;amount=1-3} ~onDeath'
    - 'dropItem{item=minecraft:leather;amount=0-2;chance=0.5} @self ~onDeath'
```

`item` names an item from the Items registry; `amount` is an
inclusive integer or `min-max` (0–64), and `chance` ranges 0–1.
Short aliases `i`, `a`, and `c` are accepted. The Items mechanic
uses `IShardItemDropSink` provided by Gameplay for physical spawning.

The Entities manager queues one-time death events after lethal
`TryDamage` or `entitykill`. Gameplay dispatches `onDeath`
through Core, passing an optional vanilla fallback for custom
entities. Gameplay owns ground-item physics/pickup/persistence;
`entityremove` never dispatches a death event. Use `skillinfo cow`
to inspect the combined registered skills.

The Entities Shard now registers `~onMilk` and `~onShear` in Core
and exposes `IShardEntityInteractions`. Gameplay validates the Java
26.3 `interact` packet (0x1A), reach and main-hand item before
applying the interaction. Cows, goats and mooshrooms convert a held
empty bucket into a milk bucket (including safe stacked-bucket
inventory handling). Sheep are sheared only once per spawn; their
wool comes from the Items Shard's `dropItem` skill:

```yaml
sheep:
  Skills:
    - 'dropItem{item=minecraft:mutton;amount=1-2} ~onDeath'
    - 'dropItem{item=minecraft:white_wool;amount=1-3} ~onShear'
```

Both triggers can be used by any Shard's skills. For example,
an administrator could add `dropItem{item=minecraft:emerald;amount=1}`
with `~onMilk` to the cow skills, creating an additional reward.
The built-in physical actions do not require a skill definition;
the skill dispatcher runs **after a successful interaction**.

The initial implementation has the `dropItem` mechanic plus
`~onDeath`, `~onMilk` and `~onShear` triggers.
Other mechanics and triggers can be added by enabled Shards;
there is no full MythicMobs runtime or advanced Minecraft loot
condition/enchantment support yet. Sheep wool colour, visual
sheared-flag metadata, grazing/regrowth, mooshroom transformations,
and shears durability on this interaction remain future work.

Old experimental `items/loot/*.yml` files with active `Drops:`
entries require manual migration to `Skills:` under
`items/skills/`. The Items Shard fails startup with an explicit
message rather than silently ignoring them. See the
[Items Shard guide](shards/Amethyst.Shards.Items/README.md).

## Build / run

```bash
dotnet build Amethyst.slnx
# Builds and installs each Shard as shards/installed/<id>/{shard.yaml,*.dll}
dotnet run --no-build --project src/Amethyst.csproj -- server.yaml
```

On Linux, `./run.sh` does both steps automatically. **Do not run only `dotnet run --project src/Amethyst.csproj` on a fresh checkout**: it builds only the network kernel, not the Shard assemblies.

`shards/installed/` and `shards/data/` are ignored by Git. Configure their paths through `shardsDirectory` and `shardsDataDirectory` in `server.yaml`. The build targets always copy Shard DLLs. Gameplay, Items and Entities refresh their manifests on each build so new required/provided capabilities are available on existing installations. Overworld and Hello preserve existing installed manifests; local `enabled` edits to those providers survive rebuilds. The Items and Entities manifests' `enabled` settings are reset to their shipped defaults during builds and can be edited after building if needed. Shard-specific writable data belongs in `shards/data/<shard-id>/`; saves and operators remain at their existing paths.

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

A dependency cycle, missing required provider, duplicate ID, incompatible API version, missing DLL, or invalid manifest causes a clear startup error. The Gameplay Shard requires the `world`, `items`, `recipes`, `skills` and `entities` capabilities rather than referring directly to `overworld` or the built-in Items implementation; alternative providers can be substituted **without changing the gameplay binary**.

To replace the built-in world provider, set `enabled: false` on the installed `overworld/shard.yaml`, install a trusted Shard that declares `provides: [world]`, and set `enabled: true` on that Shard. Its enable hook must register one `IShardWorld` service. Keep the same world save only if the new provider is actually save-format and generator compatible; otherwise choose a new `worldPath`.

## Lifecycle

1. **Discover / instantiate:** the host loads each manifest and DLL, then calls `OnLoadAsync(IShardContext, ct)`.
2. **Enable:** dependencies and capabilities are resolved first, then `OnEnableAsync(ct)` is called. Register services, packet handlers, and console commands here.
3. **Disable:** dependents must be disabled first; `OnDisableAsync(ct)` runs, then the host removes that Shard's service, packet, and command registrations.
4. **Shutdown:** enabled Shards stop in reverse dependency order. Gameplay closes before the world provider, ensuring pending saves can drain safely.

Each Shard has its own noncollectible assembly load context and resolves private dependencies from its installation directory (include its dependency DLLs and `.deps.json` when distributing third-party Shards). The shared Amethyst API assembly is never loaded twice.

**Enable/disable does not unload .NET assemblies.** DLL replacement or code updates require a process restart. The Gameplay Shard refuses to disable while players are connected. World, Items and Entities providers cannot disable while an enabled Gameplay Shard depends on them.

Console commands:

```text
shards
shard enable hello
hello World
shard disable hello
shard disable gameplay
shard disable items
shard disable entities
shard disable overworld
shard enable gameplay   # automatically enables world, items, recipes and entities
skillinfo cow
entitykill 1
iteminfo minecraft:diamond_pickaxe
entityinfo minecraft:cow
entityspawn cow 0 80 0
entities
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

The first Survival loot implementation spawns a **conservative subset of
simple drops as real item entities**. Each item has a vanilla clientbound
`add_entity` packet (type 72), item stack metadata, simple 20Hz gravity
against full-block terrain, a 500ms pickup delay, collision-based collection,
and a 5-minute expiry. Entities are visible to nearby players with loaded
chunks and are removed (with the pickup animation) after collection. Items
stay on the ground when a player's inventory is full, and partial pickups
only remove the inserted quantity. Pickup updates the server-owned inventory
and synchronizes it to the client. Loose items are persisted atomically in
`<shardsDataDirectory>/gameplay/ground-items/<world-path-hash>.bin`; save
that directory with the world and player inventory.

Unsupported loot tables, Silk Touch, Fortune, multi-item stack drops,
full-block entity contents, fluid/item physics, collision shapes, hopper
logic and merge behavior remain future work. Very large numbers of entities
are capped at 8192 per world; excess drop creations are logged.
Do not expect complete vanilla-equivalent Survival progression yet.

Creative can place its palette of simple blocks, including grass blocks
(`snowy=false`), crafting tables and the furnace family, plus axis-aware
wooden logs and persistent leaves. Furnaces, smokers, and blast furnaces use
the placing player's horizontal facing. Survival can place these blocks only
when present in its selected hotbar stack; placing consumes one item.
Multi-block structures, unrelated container menus, furniture, slab/stair
shape selection, fluids, falling block physics and redstone remain unsupported.

### Workstations (first functional milestone)

Right-click these blocks to open a **real Minecraft Java 26.3 window**:

- **Crafting table (menu ID 12):** 3×3 grid, server-calculated output,
  consuming ingredients on pickup, and returning unconsumed ingredients on
  menu close or logout. Includes an initial subset of shaped recipes:
  planks from common logs, sticks, crafting tables, chests, furnaces,
  torches, smokers, blast furnaces, wooden/stone/iron pickaxes, axes and
  shovels. The client recipe book is not populated yet; manually place
  ingredients on the grid.
- **Furnace (menu ID 14):** Shared persistent input, fuel and output slots.
  Supports a conservative vanilla subset of ore smelting (iron/gold/copper),
  sand→glass, cobblestone→stone, stone→smooth stone, charcoal from logs and
  simple cooking recipes. One cooking operation normally takes 200 server
  ticks (10 seconds).
- **Smoker (menu ID 22):** Food recipes only, twice as fast (100 ticks).
- **Blast furnace (menu ID 10):** Ore and raw-metal recipes only, twice as
  fast (100 ticks).

Furnaces consume fuel (coal/charcoal, logs, planks and sticks in this first
version), display burn/progress bars, and toggle their world block's lit state.
Cooking continues while menus are closed and the items/progress are persisted
atomically under
`<shardsDataDirectory>/gameplay/stations/<world-path-hash>.bin`.
Multiple players may open the same station. Breaking a station drops its
contents as ground items and closes any viewers' menus.

**Gameplay constraints:** The server validates the window ID, state revision,
slot transaction, current ingredients and output instead of trusting client
item claims. Basic left/right click, inventory hotbar swaps and shift transfers
are supported; advanced drag, recipe-book autoplace, number-of-recipes
completeness, tool durability, XP, fuel remainders, NBT/component recipes,
and other job-site blocks (anvils, smithing tables, brewing stands,
stonecutters, etc.) are not implemented yet. This is intentionally not the
full vanilla recipe set. Crafting's 3×3 inputs are temporary and returned
on close; furnace storage is tied to the block position.

### Inventory

Gameplay owns authoritative 36-slot main inventory + hotbar data (menu slots
9–44), selected hotbar index, and cursor. Basic pickup/right-click, shift-click
and number-key swaps are handled server-side; client changed-slot hashes are
not trusted. Armor, crafting, offhand behavior, player-initiated drop actions,
drag distribution, and component-bearing/custom items are not yet implemented. Unsupported clicks
are reconciled with the server's contents. Player inventory and cursor data are
persisted atomically under `shards/data/gameplay/inventories/<uuid>.bin` (or
the configured `shardsDataDirectory`). Back up this directory with the world.

### Player position persistence

Gameplay saves the last server-validated position, yaw, pitch, and on-ground
state **per player UUID and world**. On reconnect, that location is restored
*before* the initial 26.3 position/rotation teleport and the first chunks are
streamed, so a player resumes where they left off instead of world spawn.
First-time players use the world's generated spawn. Position persistence is
independent of `defaultGameMode`; the configured gamemode still applies on
each login.

Position files are stored atomically under
`<shardsDataDirectory>/gameplay/positions/<world-path-hash>/<player-uuid>.bin`.
Files also record the world seed and dimension to prevent restoring coordinates
from an incompatible world. Data is saved when a fully connected player leaves
and every 15 seconds while online, limiting loss after a crash. Disconnections
before the client finishes loading cannot overwrite an earlier valid location.
Invalid, damaged, or incompatible saves fall back to world spawn.

Backup `positions/` alongside `inventories/`, `ground-items/`, and the
world's `chunks/` and `edits/` directories. This first pass does not yet
implement beds, respawn anchors, death/respawn mechanics, or per-dimension
portal travel.

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
change into Survival using `/gamemode survival`, mine dirt by hand and
watch a dropped item fall to the ground. Walk away and back toward it to
collect it; verify that inventory only changes upon pickup and that the
other client sees the same spawn and collection. Try a full inventory and
reconnect after spawning an uncollected item. Place dirt from the hotbar,
then switch to Creative to test logs and instant breaks. Restart to verify
world edits, player inventory, and loose-item persistence.
