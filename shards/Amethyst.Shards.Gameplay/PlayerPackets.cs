namespace Amethyst;

public static class PlayerPackets
{
    // Player entity ID for the 26.3 vanilla entity registry (not the entity EID).
    public const int PlayerEntityType = 159;

    public static async Task SendVisibleAsync(PacketChannel receiver, ConnectedPlayer subject, CancellationToken ct)
    {
        await SendTabEntryAsync(receiver, subject, ct);
        PlayerPose pos = subject.Pose;
        await receiver.SendAsync(0x01, p =>
        {
            p.WriteVarInt(subject.EntityId);
            p.WriteUuid(subject.PlayerUuid);
            p.WriteVarInt(PlayerEntityType);
            p.WriteDouble(pos.X); p.WriteDouble(pos.Y); p.WriteDouble(pos.Z);
            p.WriteByte(0); // LpVec3 zero velocity
            p.WriteByte(ToAngle(pos.Pitch));
            p.WriteByte(ToAngle(pos.Yaw));
            p.WriteByte(ToAngle(pos.Yaw));
            p.WriteVarInt(0); // auxiliary data
        }, ct);
        ItemStack chest = subject.Inventory.Snapshot().Slots[6];
        if (chest.Count > 0)
            await FlightProtocol.SendElytraEquipmentAsync(receiver,
                subject.EntityId, chest, ct);
        if (subject.Flight.IsGliding)
            await FlightProtocol.SendGlidingAsync(receiver,
                subject.EntityId, true, ct);
    }

    public static Task SendTabEntryAsync(PacketChannel receiver, ConnectedPlayer subject, CancellationToken ct) =>
        receiver.SendAsync(0x47, p =>
        {
            p.WriteByte(0x1D);          // ADD_PLAYER, GAME_MODE, LISTED, LATENCY
            p.WriteVarInt(1);           // 1 player entry
            p.WriteUuid(subject.PlayerUuid);
            p.WriteString(subject.Username);
            p.WriteVarInt(subject.Properties.Length);
            foreach (var prop in subject.Properties)
            {
                p.WriteString(prop.Name); p.WriteString(prop.Value);
                p.WriteBool(prop.Signature is not null);
                if (prop.Signature is not null) p.WriteString(prop.Signature);
            }
            p.WriteVarInt((int)subject.GameMode); // current game mode
            p.WriteBool(true);          // listed in tab list
            p.WriteVarInt(0);           // initial ping
        }, ct);

    public static async Task SendGoneAsync(PacketChannel receiver, ConnectedPlayer subject, CancellationToken ct)
    {
        await receiver.SendAsync(0x4E, p => { p.WriteVarInt(1); p.WriteVarInt(subject.EntityId); }, ct);
        await receiver.SendAsync(0x46, p => { p.WriteVarInt(1); p.WriteUuid(subject.PlayerUuid); }, ct);
    }

    public static async Task SendMotionAsync(PacketChannel receiver, int entityId,
        PlayerPose oldPose, PlayerPose newPose, CancellationToken ct)
    {
        bool moved = oldPose.X != newPose.X || oldPose.Y != newPose.Y || oldPose.Z != newPose.Z;
        bool rotated = oldPose.Yaw != newPose.Yaw || oldPose.Pitch != newPose.Pitch;
        if (!moved && !rotated) return;
        double dx = newPose.X - oldPose.X;
        double dy = newPose.Y - oldPose.Y;
        double dz = newPose.Z - oldPose.Z;
        bool useAbsolute = moved && (Math.Abs(dx) >= 7.9 || Math.Abs(dy) >= 7.9 || Math.Abs(dz) >= 7.9);

        if (useAbsolute)
        {
            await receiver.SendAsync(0x23, p =>
            {
                p.WriteVarInt(entityId);
                p.WriteVarInt(0);       // linear position path
                p.WriteDouble(newPose.X); p.WriteDouble(newPose.Y); p.WriteDouble(newPose.Z);
                p.WriteFloat(newPose.Yaw); p.WriteFloat(newPose.Pitch);
                p.WriteBool(newPose.OnGround);
            }, ct);
        }
        else if (moved)
        {
            // 26.3 relative movement's Properties VarInt low bit = on-ground,
            // upper bits = step count. Zero steps is a single linear delta.
            await receiver.SendAsync(rotated ? 0x37 : 0x36, p =>
            {
                p.WriteVarInt(entityId);
                p.WriteVarInt(newPose.OnGround ? 1 : 0);
                p.WriteInt16(ToDelta(newPose.X, oldPose.X));
                p.WriteInt16(ToDelta(newPose.Y, oldPose.Y));
                p.WriteInt16(ToDelta(newPose.Z, oldPose.Z));
                if (rotated)
                {
                    p.WriteByte(ToAngle(newPose.Yaw));
                    p.WriteByte(ToAngle(newPose.Pitch));
                }
            }, ct);
        }
        else
        {
            await receiver.SendAsync(0x39, p =>
            {
                p.WriteVarInt(entityId);
                p.WriteBool(newPose.OnGround);
                p.WriteByte(ToAngle(newPose.Yaw));
                p.WriteByte(ToAngle(newPose.Pitch));
            }, ct);
        }
        if (rotated)
            await receiver.SendAsync(0x55,
                p => { p.WriteVarInt(entityId); p.WriteByte(ToAngle(newPose.Yaw)); }, ct);
    }

    private static short ToDelta(double next, double previous) => checked((short)(
        Math.Round(next * 4096.0) - Math.Round(previous * 4096.0)));
    private static byte ToAngle(float degrees) => unchecked((byte)(int)(degrees * (256.0 / 360.0)));
}
