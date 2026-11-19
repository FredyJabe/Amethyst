namespace Amethyst;

/// <summary>Java Edition 26.3 protocol 777 entity interact packet (0x1A).
/// The three-dimensional hit offset uses Mojang's LpVec3 encoding, which
/// may occupy 1, 6, or 6+VarInt bytes. We validate and skip it because
/// server-side interaction uses authoritative entity coordinates.</summary>
public readonly record struct EntityInteraction(int EntityId, int Hand, bool SecondaryAction);

public static class EntityInteractionProtocol
{
    public const int ServerboundInteract = 0x1A;

    public static EntityInteraction Read(Packet packet)
    {
        if (packet.Id != ServerboundInteract)
            throw new InvalidDataException("Expected entity-interact packet.");
        PacketReader reader = packet.Reader();
        int entityId = reader.ReadVarInt();
        int hand = reader.ReadVarInt();
        if (entityId <= 0 || hand is < 0 or > 1)
            throw new InvalidDataException("Invalid entity interaction target/hand.");
        byte first = reader.ReadByte();
        if (first != 0)
        {
            // LpVec3's remaining fixed bytes are in little/big-endian hybrid
            // order. We don't use the offset for hit detection.
            _ = reader.ReadFixedBytes(5);
            if ((first & 4) != 0) _ = reader.ReadVarInt();
        }
        byte secondary = reader.ReadByte();
        if (secondary > 1)
            throw new InvalidDataException("Invalid interact secondary-action flag.");
        reader.RequireEnd();
        return new EntityInteraction(entityId, hand, secondary == 1);
    }
}
