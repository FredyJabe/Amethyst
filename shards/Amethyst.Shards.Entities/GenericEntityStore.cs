using System.Security.Cryptography;
using System.Text;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>Durable, world-scoped snapshots for generic YAML entity instances.
/// Sheep retain their separate AMLIVE01 store. Network IDs are deliberately
/// never persisted: EntityManager allocates fresh IDs on each process startup.
/// File changes are atomic and are rejected completely if invalid.</summary>
internal sealed class GenericEntityStore
{
    private static readonly byte[] Magic = "AMGEN001"u8.ToArray();
    private const int MaximumEntries = 10_000;
    private const long MaximumFileBytes = 2 * 1024 * 1024;
    private readonly string? _path;
    private readonly IShardEntityRegistry _definitions;

    public GenericEntityStore(string? directory, string worldPath,
        IShardEntityRegistry definitions)
    {
        _definitions = definitions;
        if (directory is null) return;
        string folder = Path.Combine(Path.GetFullPath(directory), "entities");
        Directory.CreateDirectory(folder);
        string worldKey = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(worldPath))))[..20];
        _path = Path.Combine(folder, worldKey + ".generic.bin");
    }

    /// <summary>Load valid instances or fail startup without a partial
    /// in-memory state. The allocator belongs exclusively to EntityManager.</summary>
    public ShardEntityInstance[] Load(Func<int> allocate)
    {
        if (_path is null || !File.Exists(_path)) return [];
        using var fs = File.OpenRead(_path);
        if (fs.Length < Magic.Length + sizeof(int) ||
            fs.Length > MaximumFileBytes)
            throw new InvalidDataException("Invalid generic entity save size.");
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Invalid generic entity save header.");
        int count = reader.ReadInt32();
        if (count is < 0 or > MaximumEntries)
            throw new InvalidDataException("Invalid generic entity save count.");
        var loaded = new List<ShardEntityInstance>(count);
        var uuids = new HashSet<Guid>();
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = reader.ReadBytes(16);
            if (bytes.Length != 16)
                throw new InvalidDataException("Truncated entity UUID.");
            Guid uuid = new(bytes);
            string id = reader.ReadString();
            double x = reader.ReadDouble(), y = reader.ReadDouble(),
                z = reader.ReadDouble();
            float yaw = reader.ReadSingle(), pitch = reader.ReadSingle();
            bool hasHealth = reader.ReadBoolean();
            double? hp = hasHealth ? reader.ReadDouble() : null;
            if (uuid == Guid.Empty || !uuids.Add(uuid) ||
                id.Length is < 1 or > 256 ||
                !_definitions.TryGetByIdentifier(id, out var definition) ||
                !definition.Spawnable || definition.Identifier == "minecraft:sheep" ||
                !Valid(x, y, z, yaw, pitch) ||
                hp is double health && (!double.IsFinite(health) ||
                    health <= 0 || health > 2048))
                throw new InvalidDataException("Invalid saved generic entity.");
            // A changed admin health definition cannot resurrect an entity
            // at a larger HP cap than the new active configuration allows.
            if (definition.MaxHealth is double cap)
                hp = Math.Min(hp ?? cap, cap);
            else hp = null;
            loaded.Add(new ShardEntityInstance(0, uuid, id,
                definition.ProtocolId, x, y, z, yaw, pitch, hp));
        }
        if (fs.Position != fs.Length)
            throw new InvalidDataException("Trailing generic entity save data.");
        return loaded.Select(e => e with { EntityId = allocate() }).ToArray();
    }

    public void Save(IReadOnlyCollection<ShardEntityInstance> entities)
    {
        if (_path is null) return;
        if (entities.Count > MaximumEntries)
            throw new InvalidDataException("Too many saved generic entities.");
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(fs, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(entities.Count);
                foreach (ShardEntityInstance entity in entities.OrderBy(e => e.Uuid))
                {
                    if (entity.Uuid == Guid.Empty ||
                        entity.DefinitionId.Length > 256 ||
                        !Valid(entity.X, entity.Y, entity.Z,
                            entity.Yaw, entity.Pitch))
                        throw new InvalidDataException("Cannot persist invalid entity.");
                    writer.Write(entity.Uuid.ToByteArray());
                    writer.Write(entity.DefinitionId);
                    writer.Write(entity.X); writer.Write(entity.Y); writer.Write(entity.Z);
                    writer.Write(entity.Yaw); writer.Write(entity.Pitch);
                    writer.Write(entity.Health.HasValue);
                    if (entity.Health.HasValue) writer.Write(entity.Health.Value);
                }
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Valid(double x, double y, double z,
        float yaw, float pitch) =>
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z) &&
        float.IsFinite(yaw) && float.IsFinite(pitch) &&
        Math.Abs(x) <= 29_999_984 && Math.Abs(z) <= 29_999_984 &&
        y is >= -2048 and <= 4096;
}
