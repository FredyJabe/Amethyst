namespace Amethyst;

/// <summary>Server-side shield geometry and cooldown. A shield can block
/// attacks arriving from the player's front hemisphere, not behind them.</summary>
public static class ShieldRules
{
    public const int AxeDisableMilliseconds = 5_000;

    public static bool FacesAttacker(PlayerPose defender, PlayerPose attacker)
    {
        double dx = attacker.X - defender.X;
        double dz = attacker.Z - defender.Z;
        double length = Math.Sqrt(dx * dx + dz * dz);
        if (length < 1e-6) return true;
        double yaw = defender.Yaw * Math.PI / 180;
        double fx = -Math.Sin(yaw);
        double fz = Math.Cos(yaw);
        // Vanilla shields protect the forward 180-degree horizontal arc.
        return (fx * dx + fz * dz) / length >= 0;
    }

    public static bool IsShield(ItemStack item,
        Amethyst.Shards.IShardItemRegistry? registry) =>
        item.Count == 1 && registry is not null &&
        registry.TryGetById(item.ItemId, out var definition) &&
        definition.Identifier == "minecraft:shield";

    public static bool IsAxe(ItemStack item,
        Amethyst.Shards.IShardItemRegistry? registry) =>
        item.Count == 1 && registry is not null &&
        registry.TryGetById(item.ItemId, out var definition) &&
        definition.Identifier.EndsWith("_axe", StringComparison.Ordinal);
}

/// <summary>Transient action state; safe to modify from the receive loop
/// and the server's 20Hz entity tick.</summary>
public sealed class PlayerUseState
{
    private readonly object _gate = new();
    private int _shieldHand = -1;
    private long _shieldDisabledUntil;

    public int ShieldHand { get { lock (_gate) return _shieldHand; } }

    public bool BeginShield(int hand, long now)
    {
        if (hand is < 0 or > 1) return false;
        lock (_gate)
        {
            if (now < _shieldDisabledUntil) return false;
            _shieldHand = hand;
            return true;
        }
    }

    public bool IsBlocking(long now)
    {
        lock (_gate) return _shieldHand >= 0 &&
            now >= _shieldDisabledUntil;
    }

    public void StopShield()
    {
        lock (_gate) _shieldHand = -1;
    }

    public void DisableShield(long now)
    {
        lock (_gate)
        {
            _shieldDisabledUntil = now + ShieldRules.AxeDisableMilliseconds;
            _shieldHand = -1;
        }
    }
}
