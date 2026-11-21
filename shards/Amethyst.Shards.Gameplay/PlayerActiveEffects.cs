namespace Amethyst;

/// <summary>Thread-safe server authority for the subset of vanilla effects
/// supported by the current gameplay engine. Durations are game ticks.</summary>
public sealed class PlayerActiveEffects
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Active> _effects =
        new(StringComparer.Ordinal);
    private float _absorption;
    public readonly record struct Active(string Id, int Amplifier, int RemainingTicks);
    public readonly record struct TickResult(string Id, int Amplifier,
        int RemainingTicks, bool Expired, bool Pulse);

    public Active[] Snapshot()
    {
        lock (_gate) return _effects.Values.ToArray();
    }

    public bool Apply(string id, int duration, int amplifier)
    {
        if (!id.StartsWith("minecraft:", StringComparison.Ordinal) ||
            duration is < 1 or > 72000 || amplifier is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(duration));
        lock (_gate)
        {
            if (_effects.TryGetValue(id, out var current) &&
                current.Amplifier > amplifier &&
                current.RemainingTicks >= duration) return false;
            if (id == "minecraft:absorption")
                _absorption = Math.Max(_absorption, 4f * (amplifier + 1));
            _effects[id] = new Active(id, amplifier, duration);
            return true;
        }
    }

    public bool Has(string id)
    {
        lock (_gate) return _effects.ContainsKey(id);
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (!_effects.Remove(id)) return false;
            if (id == "minecraft:absorption") _absorption = 0;
            return true;
        }
    }

    public string[] Clear()
    {
        lock (_gate)
        {
            string[] ids = _effects.Keys.ToArray();
            _effects.Clear();
            _absorption = 0;
            return ids;
        }
    }

    /// <summary>Applies Resistance and absorbs damage before health changes.
    /// Effects do not replace invulnerability frames or armor rules.</summary>
    public float Mitigate(float damage)
    {
        if (!float.IsFinite(damage) || damage < 0)
            throw new ArgumentOutOfRangeException(nameof(damage));
        lock (_gate)
        {
            if (_effects.TryGetValue("minecraft:resistance", out Active resistance))
                damage *= Math.Max(0, 1 - 0.2f * (resistance.Amplifier + 1));
            float absorbed = Math.Min(damage, _absorption);
            _absorption -= absorbed;
            return damage - absorbed;
        }
    }

    public TickResult[] Tick()
    {
        lock (_gate)
        {
            var results = new List<TickResult>();
            foreach (var (id, old) in _effects.ToArray())
            {
                int next = old.RemainingTicks - 1;
                bool expired = next <= 0;
                bool pulse = false;
                if (!expired && id == "minecraft:regeneration")
                    pulse = next % Math.Max(1, 50 >> Math.Min(old.Amplifier, 6)) == 0;
                if (!expired && id == "minecraft:poison")
                    pulse = next % Math.Max(1, 25 >> Math.Min(old.Amplifier, 5)) == 0;
                if (!expired && id == "minecraft:hunger")
                    pulse = next % 20 == 0;
                if (expired)
                {
                    _effects.Remove(id);
                    if (id == "minecraft:absorption") _absorption = 0;
                }
                else _effects[id] = old with { RemainingTicks = next };
                if (expired || pulse)
                    results.Add(new TickResult(id, old.Amplifier, next, expired, pulse));
            }
            return results.ToArray();
        }
    }
}
