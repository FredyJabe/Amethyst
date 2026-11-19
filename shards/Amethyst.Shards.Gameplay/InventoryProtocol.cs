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

    public static void WriteStack(PacketWriter writer, ItemStack stack)
    {
        writer.WriteVarInt(stack.Count);
        if (stack.Count == 0) return;
        writer.WriteVarInt(stack.ItemId);
        // 26.3 data components: damage(3), unbreakable(4),
        // enchantments(13), repair_cost(19). The item definition already
        // provides max_damage, so no redundant max_damage patch is needed.
        bool enchanted = stack.Unbreaking > 0 || stack.Mending;
        int count = (stack.Damage > 0 ? 1 : 0) +
            (stack.Unbreakable ? 1 : 0) +
            (enchanted ? 1 : 0) +
            (stack.RepairCost > 0 ? 1 : 0);
        writer.WriteVarInt(count);
        writer.WriteVarInt(0); // removed data components
        if (stack.Damage > 0)
        {
            writer.WriteVarInt(3);
            writer.WriteVarInt(stack.Damage);
        }
        if (stack.Unbreakable)
            writer.WriteVarInt(4); // unit component, no payload
        if (enchanted)
        {
            writer.WriteVarInt(13); // minecraft:enchantments
            writer.WriteVarInt((stack.Unbreaking > 0 ? 1 : 0) +
                (stack.Mending ? 1 : 0));
            if (stack.Unbreaking > 0)
            {
                writer.WriteVarInt(VanillaRegistries.IndexOf(
                    "minecraft:enchantment", "minecraft:unbreaking"));
                writer.WriteVarInt(stack.Unbreaking);
            }
            if (stack.Mending)
            {
                writer.WriteVarInt(VanillaRegistries.IndexOf(
                    "minecraft:enchantment", "minecraft:mending"));
                writer.WriteVarInt(1);
            }
        }
        if (stack.RepairCost > 0)
        {
            writer.WriteVarInt(19);
            writer.WriteVarInt(stack.RepairCost);
        }
    }

    public static Task SendContentAsync(PacketChannel channel, PlayerInventory inventory, CancellationToken ct)
    {
        var (slots, cursor, revision) = inventory.Snapshot();
        return channel.SendAsync(ClientboundSetContent, writer =>
        {
            writer.WriteVarInt(0); // player's own inventory menu
            writer.WriteVarInt(revision);
            writer.WriteVarInt(slots.Length);
            foreach (ItemStack stack in slots) WriteStack(writer, stack);
            WriteStack(writer, cursor);
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
            WriteStack(writer, slots[slot]);
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
