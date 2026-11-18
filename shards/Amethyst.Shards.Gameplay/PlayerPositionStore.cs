using System.Security.Cryptography;
using System.Text;
using Amethyst.Shards;

namespace Amethyst;

/// <summary>
/// Durable per-player location and orientation for a particular world.
/// This belongs to Gameplay, not Core. Inventories and positions have
/// independent files because positions are world-scoped.
/// </summary>
public sealed class PlayerPositionStore
{
    private static readonly byte[] Magic = "AMPOS001"u8.ToArray();
    private readonly IShardWorld _world;
    private readonly string? _directory;
    private readonly object _gate = new();

    public bool Enabled => _directory is not null;

    public PlayerPositionStore(string? dataDirectory, string worldPath, IShardWorld world)
    {
        _world = world;
        if (dataDirectory is null) return;
        // Disambiguate worlds sharing the same Gameplay Shard data directory.
        string key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(worldPath)))).Substring(0, 20);
        _directory = Path.Combine(Path.GetFullPath(dataDirectory), "positions", key);
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(Guid playerId) =>
        Path.Combine(_directory!, playerId.ToString("N") + ".bin");

    /// <summary>Don't restore invalid or cross-world positions. Fall back to
    /// the configured world spawn rather than interrupting login.</summary>
    public bool TryLoad(Guid playerId, out PlayerPose pose)
    {
        pose = default;
        if (_directory is null) return false;
        string path = PathFor(playerId);
        if (!File.Exists(path)) return false;
        try
        {
            lock (_gate)
            {
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream, Encoding.UTF8);
                if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
                    throw new InvalidDataException("Invalid position file header.");
                long seed = reader.ReadInt64();
                string dimension = reader.ReadString();
                if (seed != _world.Seed || dimension != _world.DimensionId)
                    return false;
                var candidate = new PlayerPose(reader.ReadDouble(), reader.ReadDouble(),
                    reader.ReadDouble(), reader.ReadSingle(), reader.ReadSingle(),
                    reader.ReadBoolean());
                if (stream.Position != stream.Length || !Valid(candidate))
                    throw new InvalidDataException("Invalid saved coordinates or trailing bytes.");
                pose = candidate;
                return true;
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[position] Ignoring unreadable save for {playerId}: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"[position] Ignoring invalid save for {playerId}: {ex.Message}");
        }
        return false;
    }

    public bool Valid(PlayerPose pose) =>
        double.IsFinite(pose.X) && double.IsFinite(pose.Y) && double.IsFinite(pose.Z) &&
        float.IsFinite(pose.Yaw) && float.IsFinite(pose.Pitch) &&
        Math.Abs(pose.X) <= 29_999_984 && Math.Abs(pose.Z) <= 29_999_984 &&
        pose.Y >= _world.MinY && pose.Y <= 4096 && pose.Pitch is >= -90 and <= 90 &&
        // Non-density legacy worlds have only a heightmap. If a player fell
        // through unloaded terrain, don't resume below their generated floor.
        (_world.UsesDensity || pose.Y >=
            _world.GetGroundHeight((int)Math.Floor(pose.X), (int)Math.Floor(pose.Z)) - 12);

    /// <summary>Atomic replacement prevents partial files on interruption.
    /// Call only for ready players with server-validated poses.</summary>
    public void Save(Guid playerId, PlayerPose pose)
    {
        if (_directory is null || !Valid(pose)) return;
        string path = PathFor(playerId);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        lock (_gate)
        {
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(Magic);
                    writer.Write(_world.Seed);
                    writer.Write(_world.DimensionId);
                    writer.Write(pose.X);
                    writer.Write(pose.Y);
                    writer.Write(pose.Z);
                    writer.Write(pose.Yaw);
                    writer.Write(pose.Pitch);
                    writer.Write(pose.OnGround);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
