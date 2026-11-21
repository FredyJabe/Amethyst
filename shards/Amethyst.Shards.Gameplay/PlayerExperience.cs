using System.Text;

namespace Amethyst;

/// <summary>Persisted Minecraft Java experience total and level progress.
/// Only orb collection gives XP; Mending consumes the orb's XP first.</summary>
public sealed class PlayerExperience
{
    private static readonly byte[] Magic = "AMEXP001"u8.ToArray();
    private readonly object _gate = new();
    private readonly string? _path;
    private int _total;
    public const int MaximumExperience = 1_000_000_000;

    public PlayerExperience(string? directory, Guid player)
    {
        if (directory is null) return;
        string folder = Path.Combine(Path.GetFullPath(directory),
            "experience");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, player.ToString("N") + ".bin");
        Load();
    }

    public static long RequiredTotalForLevel(int level)
    {
        if (level <= 0) return 0;
        long n = level;
        return level <= 16 ? n * n + 6 * n :
            level <= 31 ? (5 * n * n - 81 * n + 720) / 2 :
            (9 * n * n - 325 * n + 4440) / 2;
    }

    public (float Progress, int Level, int Total) Snapshot()
    {
        lock (_gate)
        {
            int low = 0, high = 1;
            while (RequiredTotalForLevel(high) <= _total) high *= 2;
            while (low + 1 < high)
            {
                int mid = low + (high - low) / 2;
                if (RequiredTotalForLevel(mid) <= _total) low = mid;
                else high = mid;
            }
            long beginning = RequiredTotalForLevel(low),
                end = RequiredTotalForLevel(low + 1);
            return ((float)(_total - beginning) / (end - beginning),
                low, _total);
        }
    }

    public void Award(int experience)
    {
        if (experience <= 0) return;
        lock (_gate)
        {
            int old = _total;
            _total = (int)Math.Min((long)MaximumExperience, (long)_total + experience);
            try { Persist(); }
            catch { _total = old; throw; }
        }
    }

    /// <summary>Subtract experience levels to pay for a future anvil
    /// transaction. Progress at the current level is preserved by fraction.</summary>
    public bool SpendLevels(int levels)
    {
        if (levels < 0) return false;
        if (levels == 0) return true;
        lock (_gate)
        {
            var (progress, level, _) = Snapshot();
            if (level < levels) return false;
            int newLevel = level - levels;
            long beginning = RequiredTotalForLevel(newLevel);
            long required = RequiredTotalForLevel(newLevel + 1) - beginning;
            int old = _total;
            _total = (int)Math.Clamp(beginning + (long)(progress * required),
                0, MaximumExperience);
            try { Persist(); }
            catch { _total = old; throw; }
            return true;
        }
    }

    /// <summary>Reserve levels while committing one server-authoritative
    /// transaction. A stale/failed inventory commit refunds the original XP.
    /// The experience lock serializes pickups with anvil payments.</summary>
    public bool TryPayLevels(int levels, Func<bool> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (levels < 0 || levels > 39) return false;
        lock (_gate)
        {
            var (progress, level, _) = Snapshot();
            if (level < levels) return false;
            if (levels == 0) return commit();
            int newLevel = level - levels;
            long baseXp = RequiredTotalForLevel(newLevel);
            long delta = RequiredTotalForLevel(newLevel + 1) - baseXp;
            int old = _total;
            _total = (int)Math.Clamp(baseXp + (long)(progress * delta),
                0, MaximumExperience);
            try
            {
                Persist();
                if (commit()) return true;
            }
            catch
            {
                _total = old;
                Persist();
                throw;
            }
            _total = old;
            Persist();
            return false;
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        using var stream = File.OpenRead(_path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Invalid player XP header.");
        int xp = reader.ReadInt32();
        if (xp is < 0 or > MaximumExperience ||
            stream.Position != stream.Length)
            throw new InvalidDataException("Invalid player XP value.");
        _total = xp;
    }

    private void Persist()
    {
        if (_path is null) return;
        string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(_total);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
