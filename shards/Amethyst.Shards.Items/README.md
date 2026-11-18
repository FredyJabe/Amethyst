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
  Options:
    StackSize: 8
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

`MaxStackSize` (or MythicMobs-compatible
`Options.StackSize`) must be at least 1 and **cannot exceed the
vanilla base item's actual maximum** (1, 16 or 64). Specify at most
one of them. Lowering it is enforced by the server in inventory,
pickups and station transactions; vanilla clients may still predict
the original maximum until reconciled.

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
| `Options.StackSize` | integer | MythicMobs-style synonym for `MaxStackSize` |
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

## YAML recipes

The Items Shard also owns a server-authoritative
`IShardRecipeRegistry` (capability `recipes`). The bundled starter
recipes formerly hard-coded in Gameplay are now defined in
`shards/installed/items/recipes-26.3.yml`, built from
`shards/Amethyst.Shards.Items/recipes-26.3.yml`.

**Administrator recipes** go into
`shards/data/items/recipes/*.yml` or `*.yaml`, with nested directories
supported. The Items Shard creates a commented
`shards/data/items/recipes/example-recipes.yml` on first startup.
Edits require a server restart; custom files are never overwritten.
The same recipe root ID in a user file **replaces the bundled recipe**;
duplicate recipe IDs in two user files are rejected. Recipe IDs are
identifiers for server recipes, **not** vanilla protocol IDs.

### Shaped recipes

```yaml
custom_pickaxe:
  Type: Shaped
  Pattern:
    - "DDD"
    - " S "
    - " S "
  Key:
    D: minecraft:diamond
    S: minecraft:stick
  Result:
    Id: minecraft:diamond_pickaxe
    Amount: 1
```

Rows must have equal width, up to 3×3, and space means an empty slot.
Patterns match at any offset in the 3×3 crafting grid; mirrored patterns
are also supported. Every non-space symbol needs an entry under `Key`.
A key can be a single item or a **list of alternatives**:

```yaml
stick_from_wood:
  Type: Shaped
  Pattern: ["P", "P"]
  Key:
    P: [minecraft:oak_planks, minecraft:spruce_planks]
  Result: { Id: minecraft:stick, Amount: 4 }
```

### Shapeless recipes

```yaml
quick_torches:
  Type: Shapeless
  Ingredients:
    - minecraft:coal
    - minecraft:stick
  Result:
    Id: minecraft:torch
    Amount: 8
```

Each `Ingredients` entry consumes exactly one item from one grid slot.
Ingredient order and positioning do not matter, and all other grid
slots must be empty. Up to nine ingredients are supported. For alternative
materials, use a nested list, e.g.
`- [minecraft:coal, minecraft:charcoal]`.

### Furnace, smoker and blast furnace

```yaml
iron_from_stone:
  Type: Furnace
  Ingredient: minecraft:stone
  Result: { Id: minecraft:iron_ingot, Amount: 1 }
  CookingTime: 160

fast_cooked_beef:
  Type: Smoker
  Ingredient: minecraft:beef
  Result: { Id: minecraft:cooked_beef, Amount: 1 }
  CookingTime: 60

gold_from_dirt:
  Type: BlastFurnace
  Ingredient: minecraft:dirt
  Result: { Id: minecraft:gold_ingot, Amount: 1 }
  CookingTime: 100
```

Cooking types accept one `Ingredient`, which may also be a list of
alternative materials. `CookingTime` is a positive number of **server
ticks** (20 ticks per second) between 1 and 32767, defaulting to
200 for furnaces and 100 for smokers/blast furnaces.
Fuel types and fuel burn duration still belong to Gameplay; a recipe
only defines input, result and processing time.

### Disable or override a default

```yaml
# Same ID as a bundled recipe; remove it entirely:
oak_planks_from_log:
  Enabled: false

# Replace its recipe/output; custom recipes win if another recipe shares
# the same inputs, unless you override the Priority explicitly:
sticks:
  Type: Shaped
  Pattern: ["P", "P"]
  Key:
    P: [oak_planks, birch_planks, spruce_planks]
  Result: { Id: stick, Amount: 6 }
```

Optional `Priority` controls which matching recipe wins. Bundled recipes
default to 0; custom recipes default to 100, with accepted values
-1000 to 1000. The first matching recipe is selected by descending
priority, then recipe ID, to make conflicts deterministic.

### Validation and limitations

Every material name or numeric item ID is resolved through
`IShardItemRegistry` **after item definition YAML overrides load**.
A custom item *alias* resolves to its existing vanilla base protocol ID
because per-stack alias identity is not implemented yet. The output
amount must respect the configured item's maximum stack size. Malformed
YAML, duplicate IDs, unknown materials, unexpected properties, invalid
pattern rows and unsafe amounts or cooking times fail startup.

The actual crafting menu, server-side ingredient consumption, inventory
revisions, furnace ticks and station persistence remain in Gameplay;
the Items Shard owns **recipe definitions and matching**. Client-side
recipe book discovery/autoplace and complete vanilla data-pack recipe/tag
loading are not implemented yet, so craft manually in the 3×3 grid.
The bundled YAML is a starter subset, not all Minecraft recipes.

Other Shards can query recipes through:
```csharp
IShardRecipeRegistry recipes =
    context.GetRequiredService<IShardRecipeRegistry>();

var grid = new ShardRecipeStack[9];
grid[4] = new ShardRecipeStack(itemId, 1);
if (recipes.TryMatchCrafting(grid, out var craft))
    context.Log($"Matched {craft.RecipeId}, output {craft.Result.ItemId}");
```

Declare `requires: [recipes]` in consumers' manifests.
The console command `recipes` lists up to 30 available recipes;
`recipes shovel` filters by recipe ID.

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
