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

`MaxStackSize` (or the fixed `StackSize` component node,
or MythicMobs-compatible `Options.StackSize`) must be at least 1 and **cannot exceed the
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

**Important: aliases are not independent item stacks yet.**
The wire identity remains the vanilla protocol ID. Canonical vanilla
overrides now emit their item components to the client, but an alias
with the same base material still cannot be distinguished from its base
item in a player's inventory. Alias-specific component patches are
available through `ShardItemDefinition.Components`, but **they are not
applied to arbitrary stacks of that base material**. Aliases cannot
change the base material's physical capacity, durability, or block nature.

### Item components (Java 26.3 / protocol 777)

For the **complete, numbered registry of all 122 components** and its
wire format coverage, see [COMPONENTS.md](COMPONENTS.md).

### Fixed YAML nodes (45 approved, 77 pending)

Amethyst's Java 26.3 registry recognizes **122 item components**.
Exactly **45 component-to-YAML-node names are fixed in
`ItemComponentNodeMap.cs`**, following the project's approved naming
conventions. No `component-nodes.yml` is created or read. The other
**77 types have no assigned YAML shorthand** until their names are
decided. The unassigned components remain addressable in `Components:`
by their full `minecraft:` registry IDs.

The 45 approved names are documented alongside the complete component
registry in [COMPONENTS.md](COMPONENTS.md), and exposed to other
Shards as `IShardItemRegistry.ComponentNodes`. Run `componentnodes`
to list all 122 identifiers with their assigned name or
`(unassigned)`. `componentnodes food` filters the list.

Use the fixed nodes inside `Options:`, `Components:`, or as top-level
fields where they do not collide with existing item metadata:

```yaml
diamond_pickaxe:
  Id: minecraft:diamond_pickaxe
  CustomName: "<gold>Amethyst Pickaxe</gold>"
  ItemModel: amethyst:custom_pickaxe
  Components:
    Rarity: epic
    GlintOverride: true
    CustomModelData:
      floats: [42.0]
      flags: []
      strings: []
      colors: []

stone:
  Options:
    CustomData: { WireHex: "0A00" }
    Glider: { Remove: true }
```

The `CustomData` value above is an illustrative raw value, not a
general-purpose custom-data encoder. A complete typed codec remains
necessary to express the more complex components in natural YAML.
`WireHex` accepts a maximum of 65,536 bytes of pre-encoded Java 26.3
**component value bytes**, not an entire Slot packet or component ID.
Incorrect bytes can cause client disconnections.
`{ Remove: true }` removes a component from the vanilla defaults.

**Naming and implementation coverage are separate:** 45 fixed field
names, 21 typed value codecs and all 122 registry IDs accessible by
canonical ID. The remaining complex codecs and game-side mechanics
such as consumption, combat and equipping still require implementation.

### Per-stack component patches (incremental support)

Gameplay now supports a canonical, authoritative `ItemStack.ComponentData`
JSON patch and an `ItemStack.WithComponents` helper. Use
`ItemStackComponents.Encode` with a map of **canonical**
`minecraft:` component identifiers to values already encoded as JSON by
the Items Shard. No new YAML shorthand names were introduced.
Per-stack patches override bundled item-definition components,
including removals. The server validates registry IDs and serializable
values before accepting them and preserves patches through
stack merging, inventory persistence, ground drops and workstation
storage. Durability and enchantment fields continue to take precedence.

The new file versions `AMINV006`, `AMDROP03`, and `AMSTAT04`
accept older persisted formats. These additions do **not** implement
all 122 typed codecs: unsupported structured values still require
explicit `WireHex`, and the remaining consumable/equipment/etc.
behavior has separate Gameplay work ahead. Arbitrary client-supplied
components are never trusted during inventory clicks.

**Mythic-style top-level fields** and the explicit `Components` map
can be used in your item definition YAML:

```yaml
diamond_pickaxe:
  Id: minecraft:diamond_pickaxe
  CustomName: "<aqua>Miner's Pickaxe</aqua>"
  ItemName: "Masterwork Pickaxe"
  ItemModel: amethyst:miners_pickaxe
  Lore:
    - "A tool forged in amethyst"
  Rarity: epic
  GlintOverride: true
  Components:
    CustomModelData:
      floats: [42.0]
      flags: [true]
      strings: [miners_pickaxe]
      colors: [16711935]
    TooltipDisplay:
      hide_tooltip: false
      hidden_components: [minecraft:enchantments]
```

`ItemModel` and `item_model` are accepted as top-level aliases of
`minecraft:item_model`. Specify a resource location like
`amethyst:miners_pickaxe`. A **matching client resource pack is
required** for a custom texture or model; configuring the component
does not install the asset.

For canonical vanilla definitions, configured component patches are
serialized into player-inventory, workstation, and dropped-item packets.
They are combined with instance durability, enchantments, and repair
cost; stack-specific properties take precedence. The existing
`CustomModelData: 123` shorthand maps to the modern
`custom_model_data.floats: [123]` format.

Accepted `Components` entries (the `minecraft:` prefix is optional):

| Java component | YAML value |
| --- | --- |
| `item_model`, `tooltip_style` | Resource location string |
| `custom_name`, `item_name` | Text string |
| `lore` | List of text strings, up to 64 |
| `rarity` | `common`, `uncommon`, `rare`, `epic` |
| `enchantment_glint_override` | Boolean |
| `unbreakable` | Boolean; true prevents server durability wear for this vanilla item |
| `max_stack_size` | Integer 1–64, not exceeding the vanilla item capacity |
| `repair_cost` | Integer 0–32767 |
| `custom_model_data` | Nonnegative integer, or map of `floats`, `flags`, `strings`, `colors` arrays |
| `tooltip_display` | `hide_tooltip` boolean and `hidden_components` list of component names |

Top-level shorthands also include `TooltipStyle`, `Rarity`,
`Unbreakable`, and `Glint`. Do not define the same component in a
shorthand and the `Components` map; duplicates are rejected.

Additional simple typed codecs handle max damage, damage, enchantability,
map IDs, dyed colors, and no-payload flag components. Other complex
components accept `WireHex` or `Remove` under an approved node
or their full canonical `minecraft:` ID; they still need typed codecs
and gameplay integration for safe data-driven behavior.

Simple `<aqua>Text</aqua>` or `<gold>Text</gold>` names are translated
into Minecraft text components. Full MiniMessage (nested tags,
gradients, events) is not yet supported. Restart the server after
editing YAML.

### Supported keys

| Key | Type | Meaning |
| --- | --- | --- |
| Root item ID | string or numeric ID | Vanilla item to override, or a custom alias |
| `Id` | vanilla name / protocol ID | Base item; required for aliases |
| `ProtocolId` | integer | Version-specific vanilla wire ID; cannot be reassigned |
| `MaxStackSize` / `StackSize` | integer | Server-side max stack, 1 through vanilla max |
| `MaxDurability` | integer | Pinned vanilla maximum damage; user overrides may repeat, but cannot change the original value |
| `Display` / `CustomName` | string | Client-visible custom name; `CustomName` is the approved component node |
| `ItemName` | string | Client-visible item name |
| `Lore` | string list | Client-visible lore text |
| `CustomModelData` | nonnegative integer | Modern custom-model-data float shorthand |
| `Hidden` | boolean | Definition metadata, not `tooltip_display` |
| `ItemModel` | resource location | Fixed node for client-visible model selector (legacy `item_model` top-level also accepted) |
| `Rarity`, `Glint`, `Unbreakable`, `TooltipStyle` | mixed | Component shorthands |
| `Components` | mapping | Validated client-visible data-component patch |
| `Options.StackSize` | integer | MythicMobs-style synonym for `MaxStackSize` |
| `Options.BlockItem` | boolean | Block-item metadata, not placement logic |
| `Options.Hidden` | boolean | Same as top-level `Hidden` |

The per-stack `Damage` is now tracked by Gameplay. `Durability` as
a custom YAML property, `Enchantments`, arbitrary `Options`, skills and
attributes are still unsupported as item-definition properties.
They cause a descriptive startup validation error rather than
silently pretending to work.

### Vanilla durability and survival mining

The bundled Java 26.3 item catalog pins `MaxDurability` for all **84
damageable vanilla items**, including tools, weapons, armor, and shears. Use
`iteminfo diamond_pickaxe` in the server console to inspect the catalog.
Custom YAML files may repeat the same `MaxDurability`, but cannot change
the version-pinned maximum without corresponding client components.

#### Mining and tool actions

Successful Survival block breaks deduct one durability for mining tools
and shears (swords lose two). Zero-hardness blocks do not wear ordinary
tools; shears have vanilla's instant-block exception. Tools break and
disappear on their last durability point. A rejected, cancelled, or
already-completed dig does not consume durability. Creative does not
consume durability.

The Gameplay Shard also handles **axe stripping and copper
scraping/unwaxing, hoe tilling, shovel paths and campfire extinguishing,
pumpkin carving with shears, and flint-and-steel ignition/relighting**.
Only a committed block-state transformation consumes wear. Vanilla
Java 26.3 block-state variants are preserved by the transform tables.

#### Enchantments and repairs

Item stacks now store the authoritative `minecraft:damage`,
`minecraft:enchantments` (Unbreaking I–III, Mending I),
`minecraft:unbreakable`, and `minecraft:repair_cost` properties.
Unbreaking rolls independently per damage point, including the reduced
armor protection chance. Unbreakable items never lose durability.

**Mending** consumes physical XP orbs collected from the world, repairs
up to two damage points per XP, and applies leftover XP to persistent
player level/progress. Supported ores spawn persistent XP orbs only on
a valid Survival harvest. Equipped damaged Mending items (selected main
hand, offhand and armor) are eligible.

Damaged items can be repaired by combining two matching items in a
crafting table (5% repair bonus). **Grindstones** support combining
matching damaged items and removing the currently modeled non-curse
enchantments. **Anvils** support matching-item and compatible-material
repairs, enchantment combination, level costs, and the 12% chance to
chip, damage, or break the anvil after a successful Survival repair.
Vanilla's full enchantment catalog, anvil naming/renaming, and
enchanting-table behavior remain separate features.

Until a full enchanting table exists, operators can grant the supported
durability enchantments in-game with:

```text
/enchant <online-player> unbreaking [level 1-3]
/enchant <online-player> mending
```

These commands use server-side inventory changes and resynchronize the
held item. In-game use requires operator level 2; the trusted server
console also accepts `enchant <player> ...` (including offline-mode
development servers). Creative packet contents are not trusted to author
enchantments.

#### Durability state and migration

Inventory, workstation transfers, crafting inputs, dropped item
entities, and pickups preserve wear, enchantments and repair cost.
Minecraft Java 26.3 receives corresponding component patches so
durability bars and enchantment tooltips display.

New persisted file versions are `AMINV004` (player inventory),
`AMSTAT03` (workstations), and `AMDROP02` (ground items).
Their readers accept the prior respective formats,
`AMINV002`/`AMINV003`, `AMSTAT01`/`AMSTAT02`, and `AMDROP01`.
XP levels are stored in `AMEXP001`; XP orbs in `AMXPORB1`.
Back up an existing world before switching branches or versions; an
older server may not understand the newer save formats.

**Known limits:** Combat damage, blocking, shearing living entities,
bows and crossbows, tridents and thrown weapons, fishing, and Elytra
gliding need their underlying gameplay systems before those events
can apply wear. Armor wear is implemented as an authoritative API but
is not invoked until player damage is modeled. Grindstone XP refunds,
the full set of enchantments and curses, and complete vanilla anvil
combination/renaming rules are not yet implemented.

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

## Minecraft Java 26.3 recipe serializer catalog

The registry now recognizes **all 22 serializers** used by Java 26.3
(including their `minecraft:` namespaced values in `Type`). This is a
catalog/schema feature, **not** a claim that every kind can be crafted in
Gameplay today.

| Vanilla serializer | Amethyst YAML `Type` | Current behavior |
| --- | --- | --- |
| `crafting_shaped` | `Shaped` | Crafting-table matching and consumption |
| `crafting_shapeless` | `Shapeless` | Crafting-table matching and consumption |
| `smelting` | `Furnace` | Furnace processing |
| `smoking` | `Smoker` | Smoker processing |
| `blasting` | `BlastFurnace` | Blast furnace processing |
| `campfire_cooking` | `Campfire` | Registry cooking lookup; no campfire world interaction yet |
| `stonecutting` | `Stonecutting` | Registry provides **all** output choices; no stonecutter menu yet |
| `smithing_transform` | `SmithingTransform` | Registry matches three inputs and copies base components; no smithing menu yet |
| `smithing_trim` | `SmithingTrim` | Registered only; trim components must be constructed |
| `brewing` | `Brewing` | Registry matches input/reagent and potion predicates; no brewing stand yet |
| `crafting_transmute` | `CraftingTransmute` | Registered only; needs input component propagation |
| `crafting_dye` | `CraftingDye` | Registered only; needs dynamic dye component calculation |
| `crafting_imbue` | `CraftingImbue` | Registered only; needs source potion component copying |
| `crafting_decorated_pot` | `CraftingDecoratedPot` | Registered only; needs pot-decoration assembly |
| `crafting_special_bannerduplicate` | `SpecialBannerDuplicate` | Registered only; dynamic component logic |
| `crafting_special_bookcloning` | `SpecialBookCloning` | Registered only; dynamic component logic |
| `crafting_special_firework_rocket` | `SpecialFireworkRocket` | Registered only; dynamic component logic |
| `crafting_special_firework_star` | `SpecialFireworkStar` | Registered only; dynamic component logic |
| `crafting_special_firework_star_fade` | `SpecialFireworkStarFade` | Registered only; dynamic component logic |
| `crafting_special_mapextending` | `SpecialMapExtending` | Registered only; dynamic component logic |
| `crafting_special_repairitem` | `SpecialRepairItem` | Registered only; dynamic durability logic |
| `crafting_special_shielddecoration` | `SpecialShieldDecoration` | Registered only; dynamic component logic |

### Extended YAML examples

All versions below use root recipe IDs (no `Recipes:` wrapper), and
`Type` also accepts the vanilla serializer name (for example,
`minecraft:stonecutting`).

```yaml
stone_bricks_from_stone:
  Type: Stonecutting
  Ingredient: minecraft:stone
  Result: { Id: minecraft:stone_bricks, Amount: 1 }

campfire_beef:
  Type: Campfire
  Ingredient: minecraft:beef
  CookingTime: 600
  Result: { Id: minecraft:cooked_beef, Amount: 1 }

upgrade_sword:
  Type: SmithingTransform
  Template: minecraft:netherite_upgrade_smithing_template
  Base: minecraft:diamond_sword
  Addition: minecraft:netherite_ingot
  Result: { Id: minecraft:netherite_sword, Amount: 1 }

sword_trim:
  Type: SmithingTrim
  Template: minecraft:sentry_armor_trim_smithing_template
  Base: minecraft:iron_chestplate
  Addition: minecraft:diamond

water_to_awkward:
  Type: Brewing
  Input:
    Item: minecraft:potion
    PotionContents: { Potion: minecraft:water }
  Reagent: { Item: minecraft:nether_wart }
  Output:
    Id: minecraft:potion
    Components:
      'minecraft:potion_contents': { potion: minecraft:awkward }

sample_transmute:
  Type: CraftingTransmute
  Input: minecraft:stone
  Material: minecraft:diamond
  Result: { Id: minecraft:diamond, Amount: 1 }

special_rocket:
  Type: minecraft:crafting_special_firework_rocket
```

Existing `RecipeCatalog` methods are preserved. The new
`IShardRecipeRegistry.Definitions` exposes every parsed recipe's
`ShardRecipeType` and an `IsExecutable` bit. This bit describes whether
the *registry can compute a result*, **not** whether Gameplay provides
the associated container or wire serialization. The new
`GetStonecuttingRecipes`, `TryMatchSmithing` and `TryMatchBrewing`
methods are available to other Shards.

Recipe output components are retained as JSON payloads in
`ShardRecipeStack.Components`. Current Gameplay inventories and packets
still transport `ItemStack` as just an ID/count. Crafting/cooking recipes
with component-bearing results are therefore excluded from legacy
workstation matching, to avoid silently deleting components.

**Outstanding work for in-game parity:** block interactions and menus for
stonecutting/smithing/brewing, campfire world cooking, generic item-tag
ingredient resolution, complete component-based ingredient predicates and
serialization/persistence, vanilla special-recipe algorithms, and import
of the remaining Java 26.3 data-pack recipes. The bundled 73 recipes
are still a starter subset; registering a serializer does not create
all its vanilla recipes.

## Public Shard API

```csharp
using Amethyst.Shards;

IShardItemRegistry items = context.GetRequiredService<IShardItemRegistry>();
if (items.TryGetByIdentifier("diamond_pickaxe", out var item))
    context.Log($"ID {item.ProtocolId}: {item.Identifier}, stack {item.MaxStackSize}");

if (items.TryGetByIdentifier("LegendaryPickaxe", out var custom))
    context.Log(custom.Display ?? custom.Identifier);

if (items.TryGetByIdentifier("diamond_pickaxe", out var configured))
{
    context.Log(configured.ItemModel ?? "vanilla model");
    if (configured.Components is not null)
        foreach (var (name, json) in configured.Components)
            context.Log($"{name} = {json}");
}
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


## Item Skills and shared triggers

An item YAML root now accepts a `Skills` list alongside `Stats`.
The shared Core engine validates the mechanics and conditions and
dispatches them for the applicable item ID. Example:

```yaml
diamond_sword:
  Skills:
    - "cancelEvent @self ~onRightClick ?chance{base=0.25}"
    - "setFlag{name=custom_action} @self ~onRightClick"
```

`cancelEvent` never interrupts the remainder of the skill list. Item
and enchantment Skills are registered into the same Items Shard pack.
Configured `~onRightClick` skills run on use-item and use-on-block,
and `~onLeftClick` runs before server-authoritative attacks and mines.
The publisher checks cancellation before committing vanilla actions.
Gameplay also publishes `~onDrop`, `~onStackDrop` (Q vs Ctrl-Q), and
`~onPickup`: cancellation of these events prevents respectively the
ground drop or inventory pickup, while subsequent Skills still execute.
Dropping stages the physical ground entity and rolls it back if the
inventory transaction cannot consume the exact held stack. Remaining
registered triggers (`onSwap`, `onEquip`, `onUnequip`) still require
their corresponding Gameplay publishers; declarations alone do not
activate them.
Custom aliases have registered definitions, but physical stacks still
need separate alias identity before their per-alias skills can fire.

## Pinned official 26.3 loot-table inputs

`vendor/minecraft-26.3-data` is a read-only Git submodule at
`mj41/mc26-data` commit `d48a84ec43b97f41a01244bf0b6ab1d7a6b97bc6`,
extracted from Mojang's Java **26.3 / protocol 777** server jar.
The submodule carries **1,447** `data/minecraft/loot_table/**/*.json`
files plus `blocks.json` mapping every numeric block state to its
canonical block and properties. The Items Shard installs these assets
and registers `IShardVanillaLootTables` and
`IShardVanillaBlockStates`. A normal `./run.sh` checkout initializes
the submodule once. For an alternate checkout, run
`git submodule update --init --recursive` before building.
The upstream dataset is credited under its accompanying MIT license.

Live mob deaths prefer the original JSON loot table, including nested
sheep-color lookups, count providers, Looting bonuses, smelting and
explicit conditions. Live Survival block breaks prefer the actual
`minecraft:blocks/<block>` JSON using the real block-state report,
with Fortune, Silk Touch and shear-tool context. Explicit user YAML
loot overrides **take precedence** over the vanilla mob tables.
Item-defined `~onBlockDrop` skills still run and can replace vanilla
drops with `cancelEvent` followed by `dropItem`.

**Important correctness boundary:** merely loading all 1,447 files
does not mean that every loot function and predicate is implemented.
The current evaluator supports common item, alternatives, group,
sequence, nested-table entries and common conditions, weighted pools,
set-count, ore Fortune, Looting count increase, explosion decay, item
damage, simple potion components and furnace smelting. Some 26.3
specialized entry types, predicate trees and modifier types still
raise a named `NotSupportedException`. Gameplay logs these and uses
the existing explicitly approximate drop fallback. This behavior is
not yet **complete vanilla parity**. The pinned dataset is the
authoritative source for implementing and testing the remaining
function families instead of guessing hard-coded results.

## Consumables, server food, and status effects

`consumables-26.3.yml` contains 42 version-pinned item use profiles
(40 foods plus milk and potion), with nutrition, **absolute** saturation,
a timed consumption duration, `AlwaysEdible`, item remainders, and
optional effects. Overrides under `shards/data/items/consumables/*.yml`
replace individual entries; all material names resolve through the
Items Shard's item registry, independently of the 45 approved
component alias names.

Gameplay validates the original held stack through the use animation,
does not consume food before the configured use duration, cancels on
early release, and atomically decrements exactly that held stack.
Successful consumption updates persisted hunger and saturation and
returns bowls or bottles through inventory or as physical dropped
items if full. Hunger exhaustion is persisted with backwards-readable
`AMHP0002`. A server-side active-effects store synchronizes
`update_mob_effect` and `remove_mob_effect` packets to Java 26.3,
with the first periodic hooks for regeneration, poison and hunger.
The food event `~onConsume` can be cancelled by an item skill.

**This remains partial Phase 4:** accurate use effects from the
per-stack `minecraft:consumable`, `minecraft:potion_contents`,
`minecraft:suspicious_stew_effects` codecs, full absorption and
resistance combat interactions, ender-safe random teleportation,
all other status-effect gameplay hooks, and the rest of complex
vanilla interactions still need implementation. Current
`minecraft:potion` uses a basic profile and does not yet implement
per-bottle potion effects.

## YAML loot drops and `dropItem` (Phase 4)

`shards/Amethyst.Shards.Items/loot-26.3.yml` bundles initial
entity loot rules. The Items Shard installs this file alongside item,
recipe and enchantment YAML. Server operators can replace entity loot
packs by writing `*.yml` files to
`shards/data/items/loot/`, with one root per entity ID.

```yaml
minecraft:sheep:
  Skills:
    - "dropItem{item=minecraft:mutton;min=1;max=2;bonus=looting} @self ~onDeath"
    - "dropItem{item=minecraft:white_wool;amount=1} @self ~onDeath ?flag{name=not_sheared} ?flag{name=color_white}"
```

Each `dropItem` mechanic validates its `item` against the registered
vanilla item catalog at startup. Specify either `amount` or `min` and
`max` (1..64); optional `bonus=looting` adds a uniformly rolled
0..Looting-level count. Items are produced as runtime drop intents.
Gameplay commits them to ground entities **after all skills execute**.
They do not short-circuit on `cancelEvent`.

Sheep's 16 wool colors are chosen by `?flag{name=color_<color>}`;
`not_sheared` prevents wool from sheared sheep. The killer's
`~onEntityDrop` Stats are applied first, allowing Looting YAML to
modify drop rolls. `Skills: []` in an override disables that entity's
bundled drops (it does not restore hardcoded defaults).

The bundled YAML skills cover **sheep** and a compatibility
`minecraft:fishing` loot pack. With the pinned vanilla dataset installed,
fishing now first evaluates the actual
`minecraft:gameplay/fishing` table and its weighted fish, junk, and
treasure pools, with the rod's `fishing_luck` Stats as the luck parameter.
Treasure eligibility uses a conservative 5x5 water-volume test with
clear air above the bobber; complete 26.3 fluid-state rules and weather
modifiers still need to be implemented. Unknown specialized loot
modifiers trigger a logged fallback to the bundled fishing YAML.

The six mutually exclusive `?range{name=loot_roll;min=...;max=...}`
rules are retained as that fallback and for isolated tests. Explicit
user `minecraft:fishing` YAML overrides always take precedence. Lure
continues to reduce the fishing wait interval. This is a data-driven
foundation, **not** complete vanilla loot-function coverage; many
specialized tables still need interpreters and world context.

## Skill-based enchantments (Java 26.3)

The Items Shard registers all **43 vanilla enchantments** from
`enchantments-26.3.yml` as source-validated registry entries and exposes
`IShardEnchantmentRegistry`. Administrators override individual keys in
`shards/data/items/enchantments/*.yml` (or the installed Items shard's data directory).
An override replaces only the keys it supplies. `Stats` replaces
the complete stat-modifier list for that enchantment; `Skills` replaces
the skill list independently. The server rejects unknown vanilla IDs,
duplicate overrides, invalid max levels, unknown conditions and mechanics.

### Stats (items and enchantments)

Use top-level `Stats` to declare **numeric modifications**; `modify`
is no longer a skill mechanic. This format is identical in
`shards/data/items/definitions/*.yml` and
`shards/data/items/enchantments/*.yml`:

```yaml
# In an item definition
diamond_sword:
  Stats:
    damage:
      Trigger: onAttack
      Base: 3
      Operation: add
    knockback: 0.25 # Shorthand: constant additive bonus on all relevant events

# In an enchantment override (independent of the item)
smite:
  Stats:
    damage:
      Trigger: onAttack
      PerLevel: 2.5
      Conditions:
        - "target{type=undead}"

# Enchantments can keep Skills for actions, not numeric modifiers
fire_aspect:
  Skills:
    - "effect{id=fire;duration=4;perLevel=4} @target ~onAttack"
```

Each Stats key is a server-side numeric name (e.g. `damage`,
`mining_speed`, `knockback`, `projectile_damage`).
The value is a number, a mapping with `Base`, `PerLevel`,
`Operation` (`add`, `multiply`, `set`), `Trigger`, and
`Conditions`, or a list of such modifier mappings. `PerLevel`
scales with the active enchantment level; item modifiers use level 1.
Unspecified `Operation` defaults to `add`; an absent `Trigger`
applies on any event. `Conditions` accepts the same named
`?conditions` as Skills (with or without a leading `?`) and
requires all listed conditions to pass. Stats modifiers are evaluated
in YAML order, before enchantment modifiers; Skills can set flags
before the enchantment's Stats are evaluated.

Core owns the skill parser, registration, `~triggers` and `?conditions`.
Core provides `cancelEvent`; Items provides `setFlag` and `effect`
mechanics and the `level`, `target`, `damageType`, `flag`, and `chance`
conditions. Gameplay Shards must publish events and consume the resulting
numeric values and effect intents; **declaring a stat does not automatically
implement an unsupported gameplay event**.

Stats are server-side behavior, **not Minecraft item components**, and
do not change Java protocol component node assignments. As with
other item aliases, custom alias Stats currently have no separate
stack identity, so Gameplay applies physical vanilla-item Stats.

### Mending driven by Skills and Stats

Mending's `~onExperiencePickup` skill now activates repair of eligible
worn equipped items. The `mending_repair_per_xp` numeric Stat (default
2) controls how many durability points each collected XP restores.
This is evaluated synchronously during the inventory transaction,
using Core's skill engine. Setting `Skills: []` disables activation,
and overriding `Stats` changes the rate without changing compiled
Gameplay code. Eligible slots remain selected main-hand, offhand and
armor; the existing legacy Mending logic still provides a fallback
for unit-test inventories explicitly created without a skill engine.

### Unbreaking driven by Skills

The bundled Unbreaking definition has two `~onItemDamage` skill rules:

```yaml
unbreaking:
  Id: minecraft:unbreaking
  MaxLevel: 3
  Weight: 5
  AnvilCost: 2
  SupportedItems: [breakable]
  Skills:
    - "cancelEvent @self ~onItemDamage ?flag{name=non_armor} ?chance{mode=level_ratio}"
    - "cancelEvent @self ~onItemDamage ?flag{name=armor} ?chance{mode=level_ratio;factor=0.4}"
```

Every attempted durability point dispatches `~onItemDamage` through
Core's shared skill engine. When `cancelEvent` passes its conditions,
the current event is cancelled, preventing that individual durability
point of wear (including one that would have broken the item).
`?chance{mode=level_ratio}` calculates level / (level + 1);
`factor=0.4` gives armor its vanilla reduced protection chance.
`?flag{name=armor}` and `?flag{name=non_armor}` are event context
provided by the durability publisher. For other skills,
`?chance{base=0.1;perLevel=0.05}` retains linear probability.
`factor` is also available for linear chances.

Unlike the former hard-coded wear calculation, YAML overrides of
`Skills` really change or disable protection. `Skills: []` disables
Unbreaking's effect while keeping its enchantment metadata. Gameplay
consumes this rule for mining, tool use, weapons, armor, fishing rods,
Elytra and tridents (including damage on the returned trident stack).
No configured engine means no enchantment-based wear prevention.
Skill mechanics on this synchronous durability event must complete
without awaiting; this avoids asynchronous callbacks while holding
the inventory transaction lock. `cancelEvent` does **not** stop the
remaining skills: everything after the cancellation still executes,
including other enchantments' skills. The event publisher reads
`Runtime.IsCancelled` after dispatch to suppress only its default
gameplay action. A regular `setFlag` does not cancel an event.

Currently connected cancellation handlers prevent:
`~onAttack` server-side attack damage, knockback and weapon wear;
`~onHurt` health damage and armor wear; `~onMine` block destruction
(including instant and Creative mining); `~onBlockDrop` item drops;
and `~onProjectileLaunch` projectile creation and ammunition use.
Other events must honor `Runtime.IsCancelled` when implemented.
The **attacker's first-person swing is client-predicted** in vanilla
26.3, before the attack or punch packet reaches Amethyst. The server
cannot undo or conditionally suppress it via vanilla packets; fully
blocking that local animation requires a compatible client mod.
However, Amethyst now handles serverbound `punch` (0x2E), and sends
clientbound `swing_animation` (0x7B) to nearby observers on allowed
swings. If the preceding `~onAttack` is cancelled, the subsequent
swing is withheld from observers. The punch correlation lasts one
client tick to avoid accidentally suppressing later unrelated swings.
The default remote animation is a six-tick main-hand whack; item-
specific `attack_animation` variants are not yet used for this relay.

As with all server-authored enchantments, client-provided item NBT
cannot substitute for inventory authority. Vanilla item components
with definition-level Enchantments also supply enchantment levels.

This deliberately retains the **45 selected** source-fixed item-component
node aliases; unassigned nodes are still pending and are never auto-named.
