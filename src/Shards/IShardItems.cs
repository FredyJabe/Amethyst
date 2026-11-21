namespace Amethyst.Shards;

/// <summary>A version-pinned Minecraft Java item definition. The numeric ID
/// is a protocol registry ID, not a stable identifier across versions.
/// This contains identity and stacking rules only; gameplay behavior such
/// as crafting, mining, placement and consumption remains in other Shards.</summary>
public readonly record struct ShardItemDefinition(
    int ProtocolId, string Identifier, int MaxStackSize, bool IsBlockItem)
{
    /// <summary>Compatibility accessors for Mythic-style YAML shorthands.
    /// For client-visible Java 26.3 components use Components below.</summary>
    public string? Display { get; init; }
    public string? ItemName { get; init; }
    public IReadOnlyList<string>? Lore { get; init; }
    public int? CustomModelData { get; init; }
    public bool Hidden { get; init; }
    /// <summary>Optional definition-level component patch in 26.3 registry
    /// names. Keys are canonical minecraft: identifiers; values are validated
    /// JSON encoded by the Items Shard. Empty means vanilla defaults. Gameplay
    /// applies this patch to all stacks of this vanilla item ID, then overlays
    /// individual stack durability/enchantments. Alias definitions can expose
    /// separate patches, but they do not have a distinct stack identity.</summary>
    public IReadOnlyDictionary<string, string>? Components { get; init; }
    /// <summary>Convenience accessor for the minecraft:item_model component.</summary>
    public string? ItemModel { get; init; }
    /// <summary>Java 26.3 default max damage; zero means non-damageable.
    /// Physical durability is pinned to the vanilla item identity.</summary>
    public int MaxDurability { get; init; }
}

/// <summary>Read-only item registry supplied by exactly one enabled Items
/// Shard. Custom protocol IDs cannot be introduced merely by registering
/// definitions: unmodified vanilla clients require an agreed registry.</summary>
public interface IShardItemRegistry
{
    string MinecraftVersion { get; }
    int Count { get; }
    bool TryGetById(int protocolId, out ShardItemDefinition definition);
    bool TryGetByIdentifier(string identifier, out ShardItemDefinition definition);
    IReadOnlyList<ShardItemDefinition> All { get; }
    /// <summary>Canonical component ID -> configured YAML node name.
    /// This map covers every registered Java 26.3 component type, even
    /// components that currently require a raw packet payload.</summary>
    IReadOnlyDictionary<string, string> ComponentNodes { get; }
    /// <summary>Return true for the canonical empty (0,0) stack or a
    /// positive count within the item's actual maximum stack size.</summary>
    bool IsValidStack(int protocolId, int count);
}
