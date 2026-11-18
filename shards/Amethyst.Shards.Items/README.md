# Amethyst Items Shard — YAML item definitions

The Items Shard owns the **Java 26.3 / protocol 777 item catalog**.
Amethyst Core does not contain item definitions. Gameplay consumes the
`IShardItemRegistry` service for stack sizes and validations in
inventories, dropped-item entities and crafting/furnace menus.

## File locations

- Bundled vanilla definitions:
  `shards/installed/items/items-26.3.yml` (1658 items, installed from
  `shards/Amethyst.Shards.Items/items-26.3.yml` on build).
- Your editable YAML files:
  **`shards/data/items/definitions/*.yml`** and `*.yaml`,
  including nested folders. The root data directory follows the
  `shardsDataDirectory` option in `server.yaml`.
- On first startup the Shard writes
  `shards/data/items/definitions/example-items.yml` with commented
  examples. It never overwrites that file on a subsequent run.

Each file is a **root YAML mapping** whose keys are item identifiers.
There is no `Items:` wrapper. This follows the core MythicMobs format
(root item key with `Id`, `Display`, `Lore`, and `Options`).

### Override a vanilla item

```yaml
diamond_pickaxe:
  Id: minecraft:diamond_pickaxe
  Display: "<aqua>Miner's Pickaxe</aqua>"
  ItemName: "Mining Tool"
  Lore:
    - "An example description"
    - "Configured with YAML"
  MaxStackSize: 1
  Options:
    Hidden: false
    BlockItem: false

ender_pearl:
  MaxStackSize: 8
```

The root key can also be the **numeric protocol ID**, for example:

```yaml
405:
  Id: minecraft:crafting_table
  Display: "Workshop Table"
```

The `Id` field is optional for an existing vanilla root and **must
resolve to the same vanilla item** when supplied. In the bundled
`items-26.3.yml`, `ProtocolId` pins the actual wire ID. User YAML
cannot reassign protocol IDs or invent new ones.

`MaxStackSize` must be at least 1 and **cannot exceed the vanilla base
item's actual maximum** (1, 16 or 64). Lowering it is enforced by the
server in inventory, pickups and station transactions; vanilla clients
may still predict the original maximum until reconciled.

`Options.BlockItem` is metadata identifying an item that matches a
block. Changing it does *not* automatically make a block placeable or
remove Gameplay's placement restrictions. `Options.Hidden` or the
top-level `Hidden` field is read as definition metadata.

### Mythic-style aliases

You may use a distinct item root name with a vanilla `Id`:

```yaml
LegendaryPickaxe:
  Id: diamond_pickaxe
  Display: "<gold>Legendary Pickaxe</gold>"
  Lore:
    - "An alternative definition using the same vanilla item"
```

The new identifier is accessible to other Shards as
`amethyst:legendarypickaxe`, and can also be queried with
`LegendaryPickaxe`. Its base `ProtocolId` is the vanilla pickaxe ID.

**Important: aliases are metadata, not independent item stacks yet.**
Amethyst's current `ItemStack` only stores a numeric vanilla protocol ID
and count. Therefore the client cannot distinguish a legendary pickaxe
from an ordinary one, and custom alias display/lore cannot be applied
on individual inventory stacks until per-stack components/identity are
implemented. Aliases cannot override `MaxStackSize` or
`Options.BlockItem` to values different from the underlying item.

Similarly, `Display`, `ItemName`, `Lore` and
`CustomModelData` are currently **loaded, validated and exposed
through the API** but **are not serialized to Java item data
components yet**. They will not visually change the item in game in
this first migration. Never mistake metadata parsing for working
client rendering or new custom textures.

### Supported keys

| Key | Type | Meaning |
| --- | --- | --- |
| Root item ID | string or numeric ID | Vanilla item to override, or a custom alias |
| `Id` | vanilla name / protocol ID | Base item; required for aliases |
| `ProtocolId` | integer | Version-specific vanilla wire ID; cannot be reassigned |
| `MaxStackSize` | integer | Server-side max stack, 1 through vanilla max |
| `Display` | string | Optional display metadata (not yet rendered) |
| `ItemName` | string | Optional item-name metadata (not yet rendered) |
| `Lore` | string list | Optional lore metadata (not yet rendered) |
| `CustomModelData` | nonnegative integer | Optional model metadata (not yet rendered) |
| `Hidden` | boolean | Optional metadata |
| `Options.BlockItem` | boolean | Block-item metadata, not placement logic |
| `Options.Hidden` | boolean | Same as top-level `Hidden` |

`Durability`, `Enchantments`, arbitrary `Options`, skills and
attributes are not supported by this version of the Items Shard.
They will cause a descriptive startup validation error rather than
silently pretending to work.

### Multiple files and errors

All `*.yml` / `*.yaml` files are read recursively in deterministic
path order. A vanilla root in an override file replaces the corresponding
properties from the bundled catalog. **Do not define the same root in
two user files**; this is rejected to prevent accidental overrides.
Duplicate keys inside a file, unknown properties, nonexistent base
materials, invalid types, and unsafe stack counts fail startup.

Edits require a **server restart**; there is no hot reload yet. If a
previously persisted inventory contains an invalid count under a new
`MaxStackSize`, it may fail to load. Back up `inventories/` before
lowering stack limits on an existing server.

## Public Shard API

```csharp
using Amethyst.Shards;

IShardItemRegistry items = context.GetRequiredService<IShardItemRegistry>();
if (items.TryGetByIdentifier("diamond_pickaxe", out var item))
    context.Log($"ID {item.ProtocolId}: {item.Identifier}, stack {item.MaxStackSize}");

if (items.TryGetByIdentifier("LegendaryPickaxe", out var custom))
    context.Log(custom.Display ?? custom.Identifier);
```

Declare `requires: [items]` in the consuming Shard manifest.
The ShardHost enables the provider before Gameplay and prevents
disabling Items while a dependent Shard remains enabled.

The console command `iteminfo crafting_table`, `iteminfo 405` or
`iteminfo LegendaryPickaxe` prints the configured definition.

## Versioning

The bundled `items-26.3.yml` was generated from
`RosegoldMC/minecraft-data.cr/data/26.3/items.json` and
`blocks.json`, listing all **1,658** vanilla items (IDs 0–1657)
with names, stack sizes, and block metadata. The air entry (ID 0)
is the canonical empty-stack sentinel. `Options.BlockItem` is not a
guarantee that Gameplay currently supports placing the corresponding
block.

**Do not edit vanilla protocol IDs.** Future Minecraft updates require
regenerating and validating this bundle in coordination with block and
packet registries. Custom client IDs, textures and behavior require
additional client registry/asset/protocol work.

## Testing

```bash
dotnet build Amethyst.slnx -c Release
dotnet run --project tests/ShardTests/ShardTests.csproj -c Release --no-build
dotnet run --project tests/BlockTests/BlockTests.csproj -c Release --no-build
```
