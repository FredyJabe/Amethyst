namespace Amethyst;

public readonly record struct EntityInteraction(int EntityId, int Hand,
    bool SecondaryAction);

/// <summary>Java 26.3 serverbound Interact (0x1A) includes an LP_VEC3.
/// The compressed vector is validated and skipped: world reach/permission is
/// checked against the server's entity coordinates, never the client hit point.</summary>
public static class EntityInteractionProtocol
{
    public const int ServerboundInteract = 0x1A;
    public static EntityInteraction Read(Packet packet)
    {
        var r = packet.Reader();
        int entityId = r.ReadVarInt();
        int hand = r.ReadVarInt();
        // LP_VEC3: byte 0 means (0,0,0). Nonzero: one byte and a
        // big-endian uint32; bit 2 indicates a scale-extension VarInt.
        byte first = r.ReadByte();
        if (first != 0)
        {
            _ = r.ReadByte();
            _ = r.ReadInt32();
            if ((first & 4) != 0)
            {
                int extension = r.ReadVarInt();
                if (extension < 0 || extension > 16384)
                    throw new InvalidDataException("Invalid LP_VEC3 scale.");
            }
        }
        byte secondary = r.ReadByte();
        r.RequireEnd();
        if (entityId <= 0 || hand is < 0 or > 1 || secondary > 1)
            throw new InvalidDataException("Invalid entity interaction.");
        return new EntityInteraction(entityId, hand, secondary == 1);
    }
}
