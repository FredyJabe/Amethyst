# Amethyst Items Shard

This Shard owns **vanilla item identity and stack limits** for
**Minecraft Java 26.3 (protocol 777)**. Networking Core owns no item
definitions. Gameplay's inventory, physical item drops and station menus
consume this Shard's shared `IShardItemRegistry` service.

## What it defines

The `items-26.3.tsv` snapshot includes all **1,658 protocol item IDs**
(from 0 through 1657), canonical names (`minecraft:...`), maximum stack
sizes (1, 16 or 64), and whether the item has a corresponding block
definition. It was generated from:

- `RosegoldMC/minecraft-data.cr/data/26.3/items.json`
- `RosegoldMC/minecraft-data.cr/data/26.3/blocks.json`

The `minecraft:air` entry (ID 0) is the empty-stack sentinel, not a
usable item. `IsBlockItem` means the registry includes a block of the
same name; it **does not** mean Gameplay currently supports placing it.
Actual placement, mining, recipes and interaction behaviors are managed
by their respective Shards.

## Using the API from another Shard

```csharp
using Amethyst.Shards;

public Task OnEnableAsync(CancellationToken ct)
{
    var items = _context.GetRequiredService<IShardItemRegistry>();
    if (items.TryGetByIdentifier("minecraft:diamond_pickaxe", out var pickaxe))
        _context.Log($"ID {pickaxe.ProtocolId}, max stack {pickaxe.MaxStackSize}");
    return Task.CompletedTask;
}
```

Declare the dependency in the consumer's `shard.yaml`:

```yaml
requires:
  - items
```

The ShardHost enables the unique provider before its dependents and
prevents disabling Items while Gameplay depends on it.

## Console command

```text
iteminfo crafting_table
iteminfo minecraft:netherite_pickaxe
iteminfo 405
```

These commands print the item identifier, protocol ID, max stack size and
block-item status.

## Versioning and customization

**Do not change vanilla item IDs.** Those IDs are a version-specific
wire-protocol registry shared with the unmodified Java 26.3 client.
Arbitrarily adding `minecraft:my_item` or assigning a custom ID in
the catalog does not make a corresponding item exist in vanilla client
assets or registries. The Shard deliberately provides read-only
definitions rather than pretending to support arbitrary custom items.

When upgrading to a future Minecraft version, regenerate the catalog,
validate every ID and its max stack size against that version, update
the version pin in `ItemCatalog`, then coordinate recipe, block, and
protocol version changes in the other Shards.

Inventory file format remains unchanged. Existing persisted stacks whose
count is larger than the item's actual max stack size are now rejected
rather than silently accepted; back up player inventories before enabling
strict validation on an existing server.

## Tests

```bash
dotnet build Amethyst.slnx -c Release
dotnet run --project tests/ShardTests/ShardTests.csproj -c Release --no-build
dotnet run --project tests/BlockTests/BlockTests.csproj -c Release --no-build
```
