namespace Amethyst;

public readonly record struct ItemStack(int ItemId, int Count)
{
    public static readonly ItemStack Empty = new(0, 0);

    /// <summary>Accumulated item damage. Zero is undamaged; valid nonzero
    /// damage must be strictly below the item definition's MaxDurability.</summary>
    public int Damage { get; init; }
    /// <summary>Vanilla Unbreaking I–III and Mending I.
    /// These are authoritative item enchantments, not client input.</summary>
    public int Unbreaking { get; init; }
    public bool Mending { get; init; }
    /// <summary>Vanilla unbreakable component (prevents all item wear).</summary>
    public bool Unbreakable { get; init; }
    /// <summary>Anvil prior-work penalty; retained through item moves.</summary>
    public int RepairCost { get; init; }

    /// <summary>Additional vanilla enchantments in canonical, sorted
    /// "minecraft:id=level;..." form. Unbreaking/Mending retain their
    /// legacy fields for compatibility with older inventories.</summary>
    public string? EnchantmentData { get; init; }

    /// <summary>Canonical, immutable component patches for this specific
    /// stack. These are distinct from the vanilla item definition defaults;
    /// validated and normalized by ItemStackComponents.Encode.</summary>
    public string? ComponentData { get; init; }
    public IReadOnlyDictionary<string, string> Components =>
        ItemStackComponents.Decode(ComponentData);
    public ItemStack WithComponents(IReadOnlyDictionary<string, string> patches) =>
        this with { ComponentData = ItemStackComponents.Encode(patches) };

    public int EnchantmentLevel(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return 0;
        id = id.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase)
            ? id.ToLowerInvariant() : "minecraft:" + id.ToLowerInvariant();
        if (id == "minecraft:unbreaking") return Unbreaking;
        if (id == "minecraft:mending") return Mending ? 1 : 0;
        foreach (string entry in (EnchantmentData ?? "").Split(';',
            StringSplitOptions.RemoveEmptyEntries))
        {
            int sep = entry.LastIndexOf('=');
            if (sep > 0 && entry.AsSpan(0, sep).Equals(id.AsSpan(),
                StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(entry.AsSpan(sep + 1), out int level))
                return level;
        }
        return 0;
    }

    public IEnumerable<(string Id, int Level)> AllEnchantments()
    {
        if (Unbreaking > 0) yield return ("minecraft:unbreaking", Unbreaking);
        if (Mending) yield return ("minecraft:mending", 1);
        foreach (string entry in (EnchantmentData ?? "").Split(';',
            StringSplitOptions.RemoveEmptyEntries))
        {
            int sep = entry.LastIndexOf('=');
            if (sep <= 0 || !int.TryParse(entry.AsSpan(sep + 1), out int level))
                continue;
            yield return (entry[..sep], level);
        }
    }
}

