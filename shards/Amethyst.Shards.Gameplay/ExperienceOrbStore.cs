using System.Security.Cryptography;
using System.Text;

namespace Amethyst;

public readonly record struct ExperienceOrb(
    int EntityId, Guid Uuid, int Value, double X, double Y, double Z,
    double VelocityY, bool OnGround, long CreatedAtMs);

/// <summary>Authoritative persisted XP orbs, independent of item stacks.
/// XP is only awarded on real player collision, not when blocks break.</summary>
public sealed class ExperienceOrbStore
{
    private static readonly byte[] Magic = "AMXPORB1"u8.ToArray();
    private readonly object _gate = new();
    private readonly Dictionary<int, ExperienceOrb> _orbs = new();
    private readonly Func<int> _allocate;
    private readonly string? _path;
    private long _lastSimulation;
    private int _tick;
    public const int MaximumOrbs = 8192;
    public const long DespawnMs = 300_000;
    public const long PickupDelayMs = 500;

    public ExperienceOrbStore(string? directory, string worldPath,
        Func<int> allocate)
    {
        _allocate = allocate;
        if (directory is null) return;
        string folder = Path.Combine(Path.GetFullPath(directory), "experience-orbs");
        Directory.CreateDirectory(folder);
        string worldKey = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(worldPath))))[..20];
        _path = Path.Combine(folder, worldKey + ".bin");
        Load();
    }

    public ExperienceOrb[] Snapshot()
    {
        lock (_gate) return _orbs.Values.OrderBy(x => x.EntityId).ToArray();
    }

    public bool TrySpawn(int value, double x, double y, double z,
        out ExperienceOrb orb, long? nowMs = null)
    {
        orb = default;
        if (value <= 0 || value > short.MaxValue ||
            !double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(z)) return false;
        lock (_gate)
        {
            if (_orbs.Count >= MaximumOrbs) return false;
            int entityId = _allocate();
            orb = new ExperienceOrb(entityId, Guid.NewGuid(), value,
                x, y, z, 0, false,
                nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _orbs.Add(entityId, orb);
            try { Persist(); }
            catch { _orbs.Remove(entityId); throw; }
            return true;
        }
    }

    public void Simulate(Amethyst.Shards.IShardBlockWorld world, long? nowMs = null)
    {
        long now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            int steps = _lastSimulation == 0 ? 1 :
                (int)Math.Clamp((now - _lastSimulation) / 50, 1, 4);
            _lastSimulation = now;
            bool removed = false;
            foreach (ExperienceOrb original in _orbs.Values.ToArray())
            {
                if (now - original.CreatedAtMs >= DespawnMs)
                {
                    _orbs.Remove(original.EntityId);
                    removed = true;
                    continue;
                }
                ExperienceOrb orb = original;
                for (int i = 0; i < steps && !orb.OnGround; i++)
                {
                    double velocity = Math.Max(-0.4, (orb.VelocityY - 0.04) * 0.98);
                    double next = orb.Y + velocity;
                    int x = (int)Math.Floor(orb.X),
                        z = (int)Math.Floor(orb.Z),
                        below = (int)Math.Floor(next - 0.001);
                    if (below >= -64 && world.GetBlockState(x, below, z) != 0)
                    {
                        orb = orb with { Y = below + 1, VelocityY = 0, OnGround = true };
                        break;
                    }
                    orb = orb with { Y = next, VelocityY = velocity };
                }
                if (orb.Y < -80)
                {
                    _orbs.Remove(orb.EntityId);
                    removed = true;
                }
                else _orbs[orb.EntityId] = orb;
            }
            if (removed || ++_tick % 20 == 0) Persist();
        }
    }

    public int TryCollect(int entityId, long? nowMs = null)
    {
        long now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            if (!_orbs.TryGetValue(entityId, out var orb) ||
                now - orb.CreatedAtMs < PickupDelayMs) return 0;
            _orbs.Remove(entityId);
            try { Persist(); }
            catch { _orbs.Add(entityId, orb); throw; }
            return orb.Value;
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
            throw new InvalidDataException("Invalid XP orb store header.");
        int count = reader.ReadInt32();
        if (count is < 0 or > MaximumOrbs)
            throw new InvalidDataException("Invalid XP orb count.");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < count; i++)
        {
            Guid uuid = new(reader.ReadBytes(16));
            int value = reader.ReadInt32();
            double x = reader.ReadDouble(), y = reader.ReadDouble(),
                z = reader.ReadDouble(), velocity = reader.ReadDouble();
            bool onGround = reader.ReadBoolean();
            long created = reader.ReadInt64();
            if (value is < 1 or > short.MaxValue ||
                !double.IsFinite(x) || !double.IsFinite(y) ||
                !double.IsFinite(z) || !double.IsFinite(velocity))
                throw new InvalidDataException("Invalid saved XP orb.");
            if (now - created >= DespawnMs) continue;
            int id = _allocate();
            _orbs.Add(id, new ExperienceOrb(id, uuid, value,
                x, y, z, velocity, onGround, created));
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Trailing XP orb data.");
    }

    private void Persist()
    {
        if (_path is null) return;
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(_orbs.Count);
                foreach (ExperienceOrb orb in _orbs.Values.OrderBy(x => x.EntityId))
                {
                    writer.Write(orb.Uuid.ToByteArray());
                    writer.Write(orb.Value);
                    writer.Write(orb.X);
                    writer.Write(orb.Y);
                    writer.Write(orb.Z);
                    writer.Write(orb.VelocityY);
                    writer.Write(orb.OnGround);
                    writer.Write(orb.CreatedAtMs);
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
