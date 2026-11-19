namespace Amethyst;

/// <summary>Minecraft Java 26.3 / protocol 777 workstation window packets.
/// Menu IDs are the minecraft:menu registry values for this version.</summary>
public static class StationProtocol
{
    public const int OpenScreen = 0x3C;
    public const int CloseScreen = 0x11;
    public const int ContainerSetData = 0x13;
    public const int CraftingMenu = 12;
    public const int FurnaceMenu = 14;
    public const int BlastFurnaceMenu = 10;
    public const int SmokerMenu = 22;
    public const int AnvilMenu = 8;
    public const int GrindstoneMenu = 15;

    public static Task OpenAsync(PacketChannel connection, int windowId,
        int menuType, string title, CancellationToken ct) =>
        connection.SendAsync(OpenScreen, p =>
        {
            p.WriteVarInt(windowId);
            p.WriteVarInt(menuType);
            p.WritePlainTextComponent(title);
        }, ct);

    public static Task CloseAsync(PacketChannel connection, int windowId, CancellationToken ct) =>
        connection.SendAsync(CloseScreen, p => p.WriteVarInt(windowId), ct);

    public static Task SendContentAsync(PacketChannel connection, int windowId,
        int revision, ItemStack[] slots, ItemStack carried, CancellationToken ct) =>
        connection.SendAsync(InventoryProtocol.ClientboundSetContent, p =>
        {
            p.WriteVarInt(windowId);
            p.WriteVarInt(revision);
            p.WriteVarInt(slots.Length);
            foreach (var item in slots) InventoryProtocol.WriteStack(p, item);
            InventoryProtocol.WriteStack(p, carried);
        }, ct);

    public static Task SendDataAsync(PacketChannel connection, int windowId,
        int property, int value, CancellationToken ct) =>
        connection.SendAsync(ContainerSetData, p =>
        {
            p.WriteVarInt(windowId);
            p.WriteInt16((short)property);
            p.WriteInt16((short)Math.Clamp(value, 0, short.MaxValue));
        }, ct);

    public static int MenuType(StationKind kind) => kind switch
    {
        StationKind.Crafting => CraftingMenu,
        StationKind.Furnace => FurnaceMenu,
        StationKind.BlastFurnace => BlastFurnaceMenu,
        StationKind.Smoker => SmokerMenu,
        StationKind.Anvil => AnvilMenu,
        StationKind.Grindstone => GrindstoneMenu,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Title(StationKind kind) => kind switch
    {
        StationKind.Crafting => "Crafting",
        StationKind.Furnace => "Furnace",
        StationKind.BlastFurnace => "Blast Furnace",
        StationKind.Smoker => "Smoker",
        StationKind.Anvil => "Anvil",
        StationKind.Grindstone => "Grindstone",
        _ => "Station"
    };
}
