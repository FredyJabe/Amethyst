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
    public string EnchantmentData { get; init; } = "";

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

public readonly record struct InventoryClick(int Window, int Revision, int Slot,
    int Button, int Mode);

/// <summary>Protocol-777 inventory wire structures. Client changed-slot hashes
/// are decoded for bounds checking, never used as authoritative item data.</summary>
public static class InventoryProtocol
{
    public const int ServerboundContainerClick = 0x12;
    public const int ServerboundContainerClose = 0x13;
    public const int ClientboundSetContent = 0x12;
    public const int ClientboundSetSlot = 0x14;

    public static void WriteStack(PacketWriter writer, ItemStack stack,
        Amethyst.Shards.IShardItemRegistry? items = null)
    {
        writer.WriteVarInt(stack.Count);
        if (stack.Count == 0) return;
        writer.WriteVarInt(stack.ItemId);

        var added = new SortedDictionary<int, Action<PacketWriter>>();
        var removed = new SortedSet<int>();
        if (items is not null && items.TryGetById(stack.ItemId, out var definition))
            ItemComponentWire.AddDefinition(added, removed, definition);

        // Authoritative instance components override a definition's defaults.
        if (stack.Damage > 0)
            added[3] = w => w.WriteVarInt(stack.Damage);
        if (stack.Unbreakable)
        {
            added[4] = static _ => { }; // 26.3 unit component
            removed.Remove(4);
        }
        var enchants = stack.AllEnchantments()
            .OrderBy(e => VanillaRegistries.IndexOf("minecraft:enchantment", e.Id))
            .ToArray();
        if (enchants.Length > 0)
        {
            added[13] = w =>
            {
                w.WriteVarInt(enchants.Length);
                foreach (var (id, level) in enchants)
                {
                    w.WriteVarInt(VanillaRegistries.IndexOf(
                        "minecraft:enchantment", id));
                    w.WriteVarInt(level);
                }
            };
        }
        if (stack.RepairCost > 0)
            added[19] = w => w.WriteVarInt(stack.RepairCost);

        foreach (int type in added.Keys) removed.Remove(type);
        writer.WriteVarInt(added.Count);
        writer.WriteVarInt(removed.Count);
        foreach (var (type, write) in added)
        {
            writer.WriteVarInt(type);
            write(writer);
        }
        foreach (int type in removed) writer.WriteVarInt(type);
    }

    public static Task SendContentAsync(PacketChannel channel, PlayerInventory inventory, CancellationToken ct)
    {
        var (slots, cursor, revision) = inventory.Snapshot();
        return channel.SendAsync(ClientboundSetContent, writer =>
        {
            writer.WriteVarInt(0); // player's own inventory menu
            writer.WriteVarInt(revision);
            writer.WriteVarInt(slots.Length);
            foreach (ItemStack stack in slots) WriteStack(writer, stack, inventory.ItemRegistry);
            WriteStack(writer, cursor, inventory.ItemRegistry);
        }, ct);
    }

    public static Task SendSlotAsync(PacketChannel channel, PlayerInventory inventory,
        int slot, CancellationToken ct)
    {
        var (slots, _, revision) = inventory.Snapshot();
        return channel.SendAsync(ClientboundSetSlot, writer =>
        {
            writer.WriteVarInt(0);
            writer.WriteVarInt(revision);
            writer.WriteInt16((short)slot);
            WriteStack(writer, slots[slot], inventory.ItemRegistry);
        }, ct);
    }

    private static void SkipHashedStack(PacketReader reader)
    {
        bool hasItem = reader.ReadByte() != 0;
        if (!hasItem) return;
        _ = reader.ReadVarInt(); // item ID
        _ = reader.ReadVarInt(); // count
        int added = reader.ReadVarInt();
        if (added is < 0 or > 64) throw new InvalidDataException("Too many hashed components.");
        for (int i = 0; i < added; i++)
        {
            _ = reader.ReadVarInt(); // component id
            _ = reader.ReadInt32(); // CRC32C
        }
        int removed = reader.ReadVarInt();
        if (removed is < 0 or > 64) throw new InvalidDataException("Too many removed components.");
        for (int i = 0; i < removed; i++) _ = reader.ReadVarInt();
    }

    public static InventoryClick ReadClick(Packet packet)
    {
        var reader = packet.Reader();
        int window = reader.ReadVarInt();
        int revision = reader.ReadVarInt();
        short slot = reader.ReadInt16();
        int button = reader.ReadByte();
        int mode = reader.ReadVarInt();
        int changedCount = reader.ReadVarInt();
        if (changedCount is < 0 or > 128)
            throw new InvalidDataException("Invalid changed-slot count.");
        for (int i = 0; i < changedCount; i++)
        {
            _ = reader.ReadInt16();
            SkipHashedStack(reader);
        }
        SkipHashedStack(reader); // cursor
        reader.RequireEnd();
        return new InventoryClick(window, revision, slot, button, mode);
    }
}
