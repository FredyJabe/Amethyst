# Amethyst Entities Shard — YAML entity definitions

The `entities` Shard owns a **Java Edition 26.3 / protocol 777** registry
and an authoritative in-memory collection of server-spawned entities.
The networking core does not contain the entity registry or entity logic.

## Paths and precedence

- Bundled vanilla catalog: `shards/Amethyst.Shards.Entities/entities-26.3.yml`
  (161 types, copied to `shards/installed/entities/` during build).
- Editable files: `shards/data/entities/definitions/**/*.yml` or `*.yaml`,
  relative to the `shardsDataDirectory` setting in `server.yaml`.
- The first start creates `example-entities.yml` with commented templates.
  It is never overwritten after creation.

YAML root keys are entity names without an `Entities:` wrapper. Files load
recursively in deterministic path order. The bundled catalog is always
loaded first and remains version-pinned. User YAML overrides existing vanilla
definitions or adds named aliases to **existing** vanilla types.

### Override a vanilla type

```yaml
cow:
  Id: minecraft:cow
  Display: "Farm Cow"
  Health: 30
  Options:
    Invulnerable: false
    Spawnable: true

zombie:
  Health: 40
  Spawnable: true
```

Root keys may be `minecraft:cow`, `cow`, or the corresponding numeric
protocol ID. The optional `Id` must resolve to the exact vanilla type.
`ProtocolId`, `Category`, `Width` and `Height` in the bundled catalog
describe the vanilla client registry and cannot be reassigned or changed
via overrides. Unrecognized fields, conflicting values, invalid numbers,
duplicate root IDs or duplicate overrides are rejected at startup.

### Create an alias

```yaml
guardian_cow:
  Id: cow
  Display: "Guardian Cow"
  Health: 100
  Options:
    Invulnerable: true
    Spawnable: true
```

This registers the alias `amethyst:guardian_cow`; both
`guardian_cow` and the namespaced form can be used in lookups and
`entityspawn`. It keeps `minecraft:cow`'s **actual client protocol ID**
and hitbox. A vanilla client does not learn a new `guardian_cow` type.

Supported fields:

| Field | Meaning |
| --- | --- |
| `Id` | Base vanilla entity ID for an alias; optional on canonical overrides |
| `ProtocolId` | Version-pinned vanilla ID; validation only, not editable |
| `Category`, `Width`, `Height` | Version-pinned client data; validation only |
| `Display` | Optional label (definition metadata; not yet transmitted to clients) |
| `Health` | Positive 0–2048 server health maximum for newly spawned entities (actual range >0) |
| `Spawnable` | Whether `IShardEntityManager.TrySpawn` accepts this definition |
| `Invulnerable` | Rejects damage through `IShardEntityManager.TryDamage` |

`Health`, `Spawnable`, and `Invulnerable` also work in `Options` (once
per field; use either top-level or nested form). When Health is not
configured, spawned entities have no implemented health/damage model.
`Spawnable` defaults to true for non-misc vanilla categories and false
for misc entities and players. `item` entities are handled by the
existing drop system instead. Enabling a special-purpose type manually
does not implement its required item, projectile or display metadata.

## Event-driven item drops

The **Core** owns `IShardSkillEngine`, YAML parsing, registration and
trigger dispatch. The **Items Shard** registers the `dropItem` action. User definitions live in
`shards/data/items/skills/*.yml`; they use entity root keys and a
MythicMobs-like skill line with a `~onDeath` trigger:

```yaml
guardian_cow:
  Skills:
    - 'dropItem{item=minecraft:diamond;amount=1-2;chance=1} ~onDeath'
```

The Entities Shard reports deaths from `entitykill` or lethal
`TryDamage`. Gameplay dispatches Core's `onDeath` trigger, executing
the Items Shard's registered `dropItem` mechanic and
spawns physical ground items, respecting the Items registry's stack
limits. `entityremove` never triggers death skills. Alias-specific
skills take precedence, with fallback to the vanilla type when an
alias has no skills. `Skills: []` explicitly suppresses fallback.
Other triggers and actions are not implemented yet.

See [the Items Shard guide](../Amethyst.Shards.Items/README.md)
for parameters and YAML validation rules.

## Console commands

From the server terminal:

```text
entityinfo cow
entityinfo minecraft:zombie
entityinfo 72
entityspawn guardian_cow 0 80 0
entities
entityremove 1
entitykill 2
```

`entityspawn` takes a definition ID and **absolute** coordinates.
The assigned runtime ID appears in the log. A nearby player sees spawned
entities after their relevant chunks load; players moving out of range
receive remove packets. `entityremove` despawns the instance. Set
`Spawnable: false` to prevent new instances of a type.

## Public Shard API

A consumer declares `requires: [entities]` in its manifest and resolves
the interfaces from `IShardContext`:

```csharp
IShardEntityRegistry types = context.GetRequiredService<IShardEntityRegistry>();
IShardEntityManager entities = context.GetRequiredService<IShardEntityManager>();

if (types.TryGetByIdentifier("minecraft:cow", out var cow) &&
    entities.TrySpawn("cow", 12.5, 80, -4.5, out var spawned))
{
    context.Log($"Spawned {cow.Identifier} as {spawned.EntityId}");
    entities.TryTeleport(spawned.EntityId, 13.5, 80, -4.5, 0, 0);
    entities.TryDamage(spawned.EntityId, 3, out _);
}
```

The manager synchronizes its state with a lock, returns snapshots,
allocates unique positive network entity IDs through `AllocateEntityId`,
and reserves those IDs for players and dropped items through Gameplay.
The manager currently allows at most 10,000 live managed instances and
rejects non-finite or unreasonable coordinates.

## Current limits

Entity instances do not currently perform natural spawning, wandering,
pathfinding, gravity, collision, attacks, breeding, item drops, or automated
healing. Configured Health and Invulnerable currently affect manager
operations, not client-visible damage animations or a vanilla combat
system. Configured Display is not yet sent as custom-name entity metadata.
Entities are **not persisted** on restart or Shard disable. Player
entities and dropped-item entities still have dedicated Gameplay logic;
the Entities Shard supplies them a common ID allocator, not their whole
movement/inventory lifecycle.

Minecraft Java clients require the exact protocol registry types. YAML
cannot create modded entity models, custom renderer IDs, or new client
registry entries. The bundled metadata was obtained from
[mj41/mc26-data](https://github.com/mj41/mc26-data/tree/mc-26.3),
the pinned Minecraft Java 26.3 extraction.

To apply changes, restart the server. For a controlled in-process reload
while no players are connected, disable Gameplay, disable Entities, then
enable Gameplay (which automatically enables its required providers).
