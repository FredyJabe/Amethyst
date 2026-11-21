using System.Text;

namespace Amethyst;

/// <summary>Authoritative persisted player health. Attacks are serialized
/// through the health lock; invulnerability and attack-rate checks use
/// monotonic time and cannot be bypassed by forged client packets.</summary>
public sealed class PlayerVitals
{
    private static readonly byte[] Magic = "AMHP0002"u8.ToArray();
    private static readonly byte[] LegacyMagic = "AMHP0001"u8.ToArray();
    private readonly object _gate = new();
    private readonly string? _path;
    private float _health = 20;
    private int _food = 20;
    private float _saturation = 5;
    private float _exhaustion;
    private long _lastDamageAt = long.MinValue / 2;
    private long _lastAttackAt = long.MinValue / 2;
    public const float MaximumHealth = 20f;

    public PlayerVitals(string? directory, Guid uuid)
    {
        if (directory is null) return;
        string folder = Path.Combine(Path.GetFullPath(directory), "health");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, uuid.ToString("N") + ".bin");
        Load();
    }

    public (float Health, int Food, float Saturation) Snapshot()
    {
        lock (_gate) return (_health, _food, _saturation);
    }

    /// <summary>Consume a validated food item. Hunger, saturation and
    /// persistence are updated under one health-store lock.</summary>
    public bool TryEat(int nutrition, float saturation, bool alwaysEdible)
    {
        if (nutrition is < 0 or > 20 || !float.IsFinite(saturation) ||
            saturation is < 0 or > 40) return false;
        lock (_gate)
        {
            if (_health <= 0 || _food == 20 && !alwaysEdible)
                return false;
            int oldFood = _food;
            float oldSaturation = _saturation;
            _food = Math.Min(20, _food + nutrition);
            _saturation = Math.Min(_food, _saturation + saturation);
            try { Persist(); }
            catch
            {
                _food = oldFood;
                _saturation = oldSaturation;
                throw;
            }
            return true;
        }
    }

    /// <summary>Server-side exhaustion accumulator. Four exhaustion
    /// points remove one saturation point, or one food point if empty.
    /// Kept across reconnects to prevent reconnect-based hunger resets.</summary>
    public void AddExhaustion(float points)
    {
        if (!float.IsFinite(points) || points < 0 || points > 100000)
            throw new ArgumentOutOfRangeException(nameof(points));
        if (points == 0) return;
        lock (_gate)
        {
            float previousExhaustion = _exhaustion;
            float previousSaturation = _saturation;
            int previousFood = _food;
            _exhaustion += points;
            while (_exhaustion >= 4)
            {
                _exhaustion -= 4;
                if (_saturation > 0)
                    _saturation = Math.Max(0, _saturation - 1);
                else if (_food > 0)
                    _food--;
            }
            try { Persist(); }
            catch
            {
                _exhaustion = previousExhaustion;
                _saturation = previousSaturation;
                _food = previousFood;
                throw;
            }
        }
    }

    public bool TryAttack(long now)
    {
        lock (_gate)
        {
            if (_health <= 0 || now - _lastAttackAt < CombatRules.MinimumAttackIntervalMs)
                return false;
            _lastAttackAt = now;
            return true;
        }
    }

    public bool TryDamage(float amount, long now, out bool killed,
        bool bypassIFrames = false)
    {
        killed = false;
        if (!float.IsFinite(amount) || amount <= 0) return false;
        lock (_gate)
        {
            if (_health <= 0 ||
                !bypassIFrames && now - _lastDamageAt < 500)
                return false;
            float previous = _health;
            _health = Math.Max(0, _health - amount);
            _lastDamageAt = now;
            killed = _health == 0;
            try { Persist(); }
            catch { _health = previous; throw; }
            return true;
        }
    }

    public void Respawn()
    {
        lock (_gate)
        {
            float old = _health, oldSat = _saturation, oldEx = _exhaustion;
            int oldFood = _food;
            long oldDamageAt = _lastDamageAt;
            _health = MaximumHealth;
            _food = 20;
            _saturation = 5;
            _exhaustion = 0;
            _lastDamageAt = Environment.TickCount64;
            try { Persist(); }
            catch
            {
                _health = old; _food = oldFood; _saturation = oldSat;
                _exhaustion = oldEx; _lastDamageAt = oldDamageAt;
                throw;
            }
        }
    }

    public void Heal(float amount)
    {
        if (amount <= 0 || !float.IsFinite(amount)) return;
        lock (_gate)
        {
            float old = _health;
            _health = Math.Min(MaximumHealth, _health + amount);
            try { Persist(); }
            catch { _health = old; throw; }
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        using var fs = File.OpenRead(_path);
        using var reader = new BinaryReader(fs, Encoding.UTF8);
        byte[] header = reader.ReadBytes(Magic.Length);
        bool legacy = header.SequenceEqual(LegacyMagic);
        if (!legacy && !header.SequenceEqual(Magic))
            throw new InvalidDataException("Invalid player health save header.");
        _health = reader.ReadSingle();
        _food = reader.ReadInt32();
        _saturation = reader.ReadSingle();
        _exhaustion = legacy ? 0 : reader.ReadSingle();
        if (!float.IsFinite(_health) || _health < 0 || _health > MaximumHealth ||
            _food is < 0 or > 20 || !float.IsFinite(_saturation) ||
            _saturation < 0 || _saturation > _food ||
            !float.IsFinite(_exhaustion) ||
            _exhaustion is < 0 or >= 4 || fs.Position != fs.Length)
            throw new InvalidDataException("Corrupted player health data.");
    }

    private void Persist()
    {
        if (_path is null) return;
        string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(fs, Encoding.UTF8))
            {
                writer.Write(Magic); writer.Write(_health);
                writer.Write(_food); writer.Write(_saturation);
                writer.Write(_exhaustion);
                writer.Flush(); fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
