using System.Security.Cryptography;
using System.Text;

namespace Amethyst;

/// <summary>Persistent simple livestock entities. Every entity has a stable
/// UUID while runtime entity IDs are reallocated on startup. No client packet
/// can invent or mutate these entities.</summary>
public readonly record struct LivingEntity(int EntityId, Guid Uuid,
    string Kind, double X, double Y, double Z, float Health,
    bool Sheared, int Color, long LastGrazeAt)
{
    public bool Alive => Health > 0;
}

public sealed class LivingEntityStore
{
    private static readonly byte[] Magic = "AMLIVE01"u8.ToArray();
    private readonly object _gate = new();
    private readonly Dictionary<int, LivingEntity> _entities = new();
    private readonly string? _path;
    private readonly Func<int> _allocate;
    public const int MaximumEntities = 512;

    public LivingEntityStore(string? directory, string worldPath,
        Func<int> allocateEntityId)
    {
        _allocate = allocateEntityId;
        if (directory is null) return;
        string folder = Path.Combine(Path.GetFullPath(directory), "entities");
        Directory.CreateDirectory(folder);
        string key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(worldPath))))[..20];
        _path = Path.Combine(folder, key + ".bin");
        Load();
    }

    public LivingEntity[] Snapshot()
    {
        lock (_gate) return _entities.Values.OrderBy(e => e.EntityId).ToArray();
    }

    public bool TryGet(int entityId, out LivingEntity entity)
    {
        lock (_gate) return _entities.TryGetValue(entityId, out entity);
    }

    public bool TrySpawnSheep(double x, double y, double z,
        out LivingEntity created, int color = 0)
    {
        created = default;
        if (color is < 0 or > 15 || !double.IsFinite(x) ||
            !double.IsFinite(y) || !double.IsFinite(z) ||
            Math.Abs(x) > 29_999_984 || Math.Abs(z) > 29_999_984 ||
            y < -64 || y > 320) return false;
        lock (_gate)
        {
            if (_entities.Count >= MaximumEntities) return false;
            created = new LivingEntity(_allocate(), Guid.NewGuid(),
                "minecraft:sheep", x, y, z, 8, false, color, 0);
            _entities.Add(created.EntityId, created);
            try { Persist(); }
            catch { _entities.Remove(created.EntityId); throw; }
            return true;
        }
    }

    public bool TryDamage(int id, float points, out LivingEntity updated,
        out bool killed)
    {
        updated = default;
        killed = false;
        if (!float.IsFinite(points) || points <= 0) return false;
        lock (_gate)
        {
            if (!_entities.TryGetValue(id, out var old)) return false;
            float next = Math.Max(0, old.Health - points);
            updated = old with { Health = next };
            killed = next == 0;
            if (killed) _entities.Remove(id);
            else _entities[id] = updated;
            try { Persist(); }
            catch
            {
                _entities[id] = old;
                throw;
            }
            return true;
        }
    }

    public bool TryShear(int id, out LivingEntity changed)
    {
        changed = default;
        lock (_gate)
        {
            if (!_entities.TryGetValue(id, out var old) || old.Sheared ||
                !old.Alive || old.Kind != "minecraft:sheep") return false;
            changed = old with { Sheared = true, LastGrazeAt = Environment.TickCount64 };
            _entities[id] = changed;
            try { Persist(); }
            catch { _entities[id] = old; throw; }
            return true;
        }
    }

    public LivingEntity[] Regrow(long nowMs, int requiredMs = 180_000)
    {
        lock (_gate)
        {
            var result = new List<LivingEntity>();
            foreach (var old in _entities.Values.ToArray())
            {
                if (!old.Sheared || nowMs - old.LastGrazeAt < requiredMs) continue;
                LivingEntity next = old with { Sheared = false, LastGrazeAt = nowMs };
                _entities[next.EntityId] = next;
                result.Add(next);
            }
            if (result.Count > 0) Persist();
            return result.ToArray();
        }
    }

    public void Save()
    {
        lock (_gate) Persist();
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        using var fs = File.OpenRead(_path);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        if (!r.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Invalid entity store header.");
        int count = r.ReadInt32();
        if (count is < 0 or > MaximumEntities)
            throw new InvalidDataException("Invalid entity count.");
        for (int i = 0; i < count; i++)
        {
            Guid uuid = new(r.ReadBytes(16));
            string kind = r.ReadString();
            double x = r.ReadDouble(), y = r.ReadDouble(), z = r.ReadDouble();
            float hp = r.ReadSingle();
            bool sheared = r.ReadBoolean();
            int color = r.ReadInt32();
            long graze = r.ReadInt64();
            if (kind != "minecraft:sheep" || !double.IsFinite(x) ||
                !double.IsFinite(y) || !double.IsFinite(z) ||
                !float.IsFinite(hp) || hp <= 0 || hp > 8 ||
                color is < 0 or > 15)
                throw new InvalidDataException("Invalid saved sheep.");
            var e = new LivingEntity(_allocate(), uuid, kind,
                x, y, z, hp, sheared, color, graze);
            _entities[e.EntityId] = e;
        }
        if (fs.Position != fs.Length)
            throw new InvalidDataException("Trailing entity data.");
    }

    private void Persist()
    {
        if (_path is null) return;
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(_entities.Count);
                foreach (var e in _entities.Values.OrderBy(v => v.EntityId))
                {
                    w.Write(e.Uuid.ToByteArray()); w.Write(e.Kind);
                    w.Write(e.X); w.Write(e.Y); w.Write(e.Z);
                    w.Write(e.Health); w.Write(e.Sheared);
                    w.Write(e.Color); w.Write(e.LastGrazeAt);
                }
                w.Flush(); fs.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
