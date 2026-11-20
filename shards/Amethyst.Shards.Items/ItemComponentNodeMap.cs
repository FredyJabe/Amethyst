using System.Collections.ObjectModel;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>Fixed, source-defined YAML node names for Minecraft Java 26.3
/// item data components. Only explicitly approved names are assigned:
/// the remaining registry types have no shorthand YAML node pending a
/// future decision. Canonical minecraft:component IDs remain usable
/// inside Components: for protocol compatibility. This is NOT a file
/// configuration system.</summary>
public sealed class ItemComponentNodeMap
{
    // These 45 names were explicitly selected for Amethyst's public YAML API.
    // Do not auto-generate names for unassigned component types.
    private const string FixedAssignments = """
minecraft:custom_data: CustomData
minecraft:max_stack_size: StackSize
minecraft:max_damage: DurabilityMax
minecraft:damage: DurabilityDamage
minecraft:unbreakable: Unbreakable
minecraft:custom_name: CustomName
minecraft:minimum_attack_charge: MinimumAttackCharge
minecraft:damage_type: DamageType
minecraft:item_name: ItemName
minecraft:item_model: ItemModel
minecraft:lore: Lore
minecraft:rarity: Rarity
minecraft:enchantments: Enchantments
minecraft:can_place_on: CanPlaceOn
minecraft:can_break: CanBreak
minecraft:attribute_modifiers: AttributesModifiers
minecraft:custom_model_data: CustomModelData
minecraft:tooltip_display: TooltipDisplay
minecraft:repair_cost: RepairCost
minecraft:creative_slot_lock: CreativeSlotLock
minecraft:enchantment_glint_override: GlintOverride
minecraft:food: Food
minecraft:consumable: Consumable
minecraft:use_remainder: UseRemainder
minecraft:use_cooldown: UseCooldown
minecraft:damage_resistant: DamageResistant
minecraft:tool: Tool
minecraft:weapon: Weapon
minecraft:attack_range: AttackRange
minecraft:enchantable: Enchantable
minecraft:equippable: Equippable
minecraft:repairable: Repairable
minecraft:glider: Glider
minecraft:tooltip_style: TooltipStyle
minecraft:death_protection: DeathProtection
minecraft:blocks_attacks: BlocksAttacks
minecraft:piercing_weapon: PiercingWeapon
minecraft:kinetic_weapon: KineticWeapon
minecraft:dyed_color: DyedColor
minecraft:instrument: Instrument
minecraft:jukebox_playable: JukeboxPlayable
minecraft:lock: Lock
minecraft:compostable: Compostable
minecraft:cooking_fuel: CookingFuel
minecraft:brewing_fuel: BrewingFuel
""";

    private readonly IReadOnlyDictionary<string, string> _nodes;
    private readonly IReadOnlyDictionary<string, string> _aliases;

    /// <summary>The 45 assigned component-ID -> fixed YAML-node mappings.</summary>
    public IReadOnlyDictionary<string, string> Nodes => _nodes;
    public int PendingCount => ShardItemComponentIds.Count - _nodes.Count;

    public ItemComponentNodeMap()
    {
        var nodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in FixedAssignments.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.LastIndexOf(": ", StringComparison.Ordinal);
            if (separator < 0)
                throw new InvalidOperationException($"Invalid fixed node mapping: {line}.");
            string component = line[..separator];
            string name = line[(separator + 2)..];
            if (!ShardItemComponentIds.TryGetId(component, out _) ||
                string.IsNullOrWhiteSpace(name) ||
                !nodes.TryAdd(component, name) ||
                !aliases.TryAdd(name, component))
                throw new InvalidOperationException(
                    $"Invalid, unknown or duplicated fixed component node {line}.");
        }
        if (nodes.Count != 45)
            throw new InvalidOperationException("Expected exactly 45 approved component node names.");

        _nodes = new ReadOnlyDictionary<string, string>(nodes);
        _aliases = new ReadOnlyDictionary<string, string>(aliases);
    }

    /// <summary>Resolve one of the 45 approved names. Fully qualified
    /// Minecraft registry IDs are accepted directly for compatibility,
    /// including unassigned components; short inferred names are not.</summary>
    public bool TryResolve(string yamlNode, out string component)
    {
        component = string.Empty;
        if (string.IsNullOrWhiteSpace(yamlNode)) return false;
        string key = yamlNode.Trim();
        if (key.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
        {
            if (ShardItemComponentIds.TryGetId(key.ToLowerInvariant(), out int id))
                return ShardItemComponentIds.TryGetIdentifier(id, out component);
            return false;
        }
        return _aliases.TryGetValue(key, out component!);
    }
}
