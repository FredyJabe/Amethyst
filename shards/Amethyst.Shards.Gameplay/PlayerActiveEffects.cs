using System.Text;

namespace Amethyst;

/// <summary>Server-owned 26.3 potion status and absorption state. Duration
/// counts only active game ticks; the store is persisted on every effect
/// change and on disconnect, not on every one of the 20 ticks per second.
/// Save files are rejected on invalid or unregistered effect IDs.</summary>
public sealed class PlayerActiveEffects
{
    private static readonly byte[] Magic = "AMEFF001"u8.ToArray();
    private readonly object _gate = new();
    private readonly Dictionary<string, Active> _effects =
        new(StringComparer.Ordinal);
    private readonly string? _path;
    private float _absorption;

    public readonly record struct Active(string Id, int Amplifier, int RemainingTicks);
    public readonly record struct TickResult(string Id, int Amplifier,
        int RemainingTicks, bool Expired, bool Pulse);

    public PlayerActiveEffects(string? directory = null, Guid? player = null)
    {
        if (directory is null) return;
        if (player is null) throw new ArgumentNullException(nameof(player));
        string folder = Path.Combine(Path.GetFullPath(directory), "effects");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, player.Value.ToString("N") + ".bin");
        Load();
    }

    public Active[] Snapshot()
    {
        lock (_gate) return _effects.Values.ToArray();
    }

    public float Absorption
    {
        get { lock (_gate) return _absorption; }
    }

    public bool Apply(string id, int duration, int amplifier)
    {
        if (!VanillaBuiltinIds.TryGet("minecraft:mob_effect", id, out _) ||
            duration is < 1 or > 72000 || amplifier is < 0 or > 255)
            throw new InvalidDataException("Invalid vanilla status effect.");
        lock (_gate)
        {
            if (_effects.TryGetValue(id, out var current) &&
                current.Amplifier > amplifier &&
                current.RemainingTicks >= duration) return false;
            var before = _effects.TryGetValue(id, out var old) ? old : (Active?)null;
            float previousAbsorption = _absorption;
            if (id == "minecraft:absorption")
                _absorption = Math.Max(_absorption, 4f * (amplifier + 1));
            _effects[id] = new Active(id, amplifier, duration);
            try { Persist(); }
            catch
            {
                if (before is Active saved) _effects[id] = saved;
                else _effects.Remove(id);
                _absorption = previousAbsorption;
                throw;
            }
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
            if (!_effects.TryGetValue(id, out var previous)) return false;
            float oldAbsorption = _absorption;
            _effects.Remove(id);
            if (id == "minecraft:absorption") _absorption = 0;
            try { Persist(); }
            catch { _effects[id] = previous; _absorption = oldAbsorption; throw; }
            return true;
        }
    }

    public string[] Clear()
    {
        lock (_gate)
        {
            var before = _effects.ToArray();
            float oldAbsorption = _absorption;
            string[] ids = _effects.Keys.ToArray();
            _effects.Clear();
            _absorption = 0;
            try { Persist(); }
            catch
            {
                foreach (var pair in before) _effects[pair.Key] = pair.Value;
                _absorption = oldAbsorption;
                throw;
            }
            return ids;
        }
    }

    /// <summary>Applies Resistance and absorbs damage after invulnerability
    /// frames have already been checked by PlayerVitals.TryDamage.</summary>
    public float Mitigate(float damage)
    {
        if (!float.IsFinite(damage) || damage < 0)
            throw new ArgumentOutOfRangeException(nameof(damage));
        lock (_gate)
        {
            if (_effects.TryGetValue("minecraft:resistance", out Active resistance))
                damage *= Math.Max(0, 1 - 0.2f * (resistance.Amplifier + 1));
            float previousAbsorption = _absorption;
            float absorbed = Math.Min(damage, _absorption);
            _absorption -= absorbed;
            try { if (absorbed > 0) Persist(); }
            catch { _absorption = previousAbsorption; throw; }
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

    public void Save()
    {
        lock (_gate) Persist();
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        using var stream = File.OpenRead(_path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Invalid status effect store header.");
        float absorption = reader.ReadSingle();
        int count = reader.ReadInt32();
        if (!float.IsFinite(absorption) || absorption is < 0 or > 1024 ||
            count is < 0 or > 40)
            throw new InvalidDataException("Invalid saved potion state.");
        var loaded = new Dictionary<string, Active>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string id = reader.ReadString();
            int amplifier = reader.ReadInt32(), ticks = reader.ReadInt32();
            if (!VanillaBuiltinIds.TryGet("minecraft:mob_effect", id, out _) ||
                amplifier is < 0 or > 255 || ticks is < 1 or > 72000 ||
                !loaded.TryAdd(id, new Active(id, amplifier, ticks)))
                throw new InvalidDataException("Invalid persisted potion effect.");
        }
        if (stream.Position != stream.Length ||
            absorption > 0 && !loaded.ContainsKey("minecraft:absorption"))
            throw new InvalidDataException("Trailing or inconsistent status data.");
        foreach (var (id, effect) in loaded) _effects.Add(id, effect);
        _absorption = absorption;
    }

    private void Persist()
    {
        if (_path is null) return;
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(_absorption);
                writer.Write(_effects.Count);
                foreach (var effect in _effects.Values.OrderBy(x => x.Id,
                    StringComparer.Ordinal))
                {
                    writer.Write(effect.Id);
                    writer.Write(effect.Amplifier);
                    writer.Write(effect.RemainingTicks);
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
