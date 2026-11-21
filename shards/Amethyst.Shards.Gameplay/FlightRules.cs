namespace Amethyst;

/// <summary>Gliding eligibility, movement safety and rocket boost equations.
/// The server owns the active state and item wear; the client sends movement
/// for its camera, which is checked against bounded physical displacement.</summary>
public static class FlightRules
{
    public const long WearIntervalMs = 1000;
    public const long RocketBoostMs = 1600;

    public static bool CanStart(bool onGround, ItemStack elytra,
        int maximum)
        => !onGround && elytra.Count == 1 && maximum > 1 &&
           elytra.Damage < maximum - 1;

    public static (double X, double Y, double Z) RocketImpulse(
        float yaw, float pitch)
    {
        double yr = yaw * Math.PI / 180,
            pr = pitch * Math.PI / 180;
        return (-Math.Sin(yr) * Math.Cos(pr) * 1.8,
            -Math.Sin(pr) * 1.8 + 0.1,
            Math.Cos(yr) * Math.Cos(pr) * 1.8);
    }

    public static bool PlausibleMovement(PlayerPose previous,
        PlayerPose current, bool boosted)
    {
        double dx = current.X - previous.X,
            dy = current.Y - previous.Y,
            dz = current.Z - previous.Z;
        // A generous packet-level bound preserves legitimate Elytra
        // movements even under short client lag without allowing >12-block
        // single-packet teleport exploits while gliding.
        double limit = boosted ? 12 : 6;
        return dx * dx + dy * dy + dz * dz <= limit * limit;
    }

    /// <summary>Lightweight approximation for server velocity checks.
    /// Pitching downward gains speed; leveling off converts momentum
    /// into lift. All integration is 20Hz.</summary>
    public static (double Vx, double Vy, double Vz) GlideStep(
        double vx, double vy, double vz, float yaw, float pitch)
    {
        double radYaw = yaw * Math.PI / 180,
            radPitch = pitch * Math.PI / 180;
        double forwardX = -Math.Sin(radYaw) * Math.Cos(radPitch),
            forwardZ = Math.Cos(radYaw) * Math.Cos(radPitch);
        double lift = Math.Pow(Math.Cos(radPitch), 2) * 0.06;
        vy += -0.08 + lift;
        if (radPitch < 0) vy += Math.Abs(radPitch) * 0.05;
        if (radPitch > 0) vy -= radPitch * 0.04;
        vx += forwardX * 0.01;
        vz += forwardZ * 0.01;
        return (vx * 0.99, vy * 0.98, vz * 0.99);
    }
}

public sealed class PlayerFlightState
{
    private readonly object _gate = new();
    private bool _gliding;
    private long _lastWearAt;
    private long _boostUntil;

    public bool IsGliding
    {
        get { lock (_gate) return _gliding; }
    }

    public bool Begin(long now)
    {
        lock (_gate)
        {
            if (_gliding) return false;
            _gliding = true;
            _lastWearAt = now;
            _boostUntil = 0;
            return true;
        }
    }

    public bool End()
    {
        lock (_gate)
        {
            if (!_gliding) return false;
            _gliding = false;
            _boostUntil = 0;
            return true;
        }
    }

    public bool Boost(long now)
    {
        lock (_gate)
        {
            if (!_gliding) return false;
            _boostUntil = Math.Max(_boostUntil, now + FlightRules.RocketBoostMs);
            return true;
        }
    }

    public bool IsBoosted(long now)
    {
        lock (_gate) return _gliding && now < _boostUntil;
    }

    public bool WearDue(long now)
    {
        lock (_gate)
        {
            if (!_gliding || now - _lastWearAt < FlightRules.WearIntervalMs)
                return false;
            _lastWearAt = now;
            return true;
        }
    }
}
