using Amethyst.Shards;

namespace Amethyst;

/// <summary>Java 26.3 Set Equipment (0x68). Each entry's high bit is set
/// when another slot follows; the last entry has no continuation bit.
/// Item stacks include definition and per-instance components.</summary>
public readonly record struct EntityEquipment(byte Slot, ItemStack Stack);

public static class EquipmentProtocol
{
    public static EntityEquipment[] AllSlots(PlayerInventory inventory)
    {
        ItemStack[] slots = inventory.Snapshot().Slots;
        int selected = inventory.SelectedSlot;
        return new[]
        {
            new EntityEquipment(0, slots[selected]),
            new EntityEquipment(1, slots[45]),
            new EntityEquipment(2, slots[8]),
            new EntityEquipment(3, slots[7]),
            new EntityEquipment(4, slots[6]),
            new EntityEquipment(5, slots[5])
        }.Where(x => x.Stack.Count > 0).ToArray();
    }

    public static Task SendAsync(PacketChannel channel, int entityId,
        IReadOnlyList<EntityEquipment> equipment,
        IShardItemRegistry? items, CancellationToken ct)
    {
        if (equipment.Count == 0) return Task.CompletedTask;
        if (entityId <= 0 || equipment.Count > 6 ||
            equipment.Any(e => e.Slot > 5) ||
            equipment.Select(e => e.Slot).Distinct().Count() != equipment.Count)
            throw new ArgumentException("Invalid player equipment update.");
        return channel.SendAsync(FlightProtocol.ClientboundSetEquipment, writer =>
        {
            writer.WriteVarInt(entityId);
            for (int i = 0; i < equipment.Count; i++)
            {
                EntityEquipment slot = equipment[i];
                writer.WriteByte((byte)(slot.Slot |
                    (i == equipment.Count - 1 ? 0 : 0x80)));
                InventoryProtocol.WriteStack(writer, slot.Stack, items);
            }
        }, ct);
    }
}
