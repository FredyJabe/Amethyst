using System.Collections.Concurrent;
using System.Text;

namespace Amethyst;

/// <summary>
/// Sparse block changes over immutable generated .mc26 chunks. Each edited chunk has
/// its own atomically replaced sidecar file; old generation snapshots remain intact.
/// </summary>
internal sealed class WorldEdits
{
    private static readonly byte[] Magic = "AMEDITS1"u8.ToArray();
    private const int CellsPerColumn = 16 * 16 * ChunkColumn.SectionCount * 16;
    private readonly string? _directory;
    private readonly ConcurrentDictionary<ChunkPos, ColumnEdits> _columns = new();

    private sealed class ColumnEdits
    {
        public readonly object Gate = new();
        public readonly Dictionary<int, ushort> States = new();
        public long Revision;
    }

    public WorldEdits(string? worldPath)
    {
        if (worldPath is null) return; // nonpersistent/legacy generators: in-memory edits only
        _directory = Path.Combine(Path.GetFullPath(worldPath), "edits");
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(ChunkPos pos) =>
        Path.Combine(_directory!, $"{pos.X}.{pos.Z}.amedit");

    private ColumnEdits Load(ChunkPos pos)
    {
        var result = new ColumnEdits();
        if (_directory is null || !File.Exists(PathFor(pos))) return result;
        using var stream = File.OpenRead(PathFor(pos));
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic) ||
            reader.ReadInt32() != pos.X || reader.ReadInt32() != pos.Z)
            throw new InvalidDataException($"Invalid edit file for {pos}.");
        int count = reader.ReadInt32();
        if (count is < 0 or > CellsPerColumn)
            throw new InvalidDataException($"Invalid edit count for {pos}.");
        for (int i = 0; i < count; i++)
        {
            int index = reader.ReadInt32();
            ushort state = reader.ReadUInt16();
            if (index is < 0 or >= CellsPerColumn || !result.States.TryAdd(index, state))
                throw new InvalidDataException($"Invalid block edit in {pos}.");
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException($"Unexpected trailing block edits in {pos}.");
        return result;
    }

    private ColumnEdits For(ChunkPos pos) => _columns.GetOrAdd(pos, Load);

    private static int Index(int x, int y, int z) =>
        (y - ChunkColumn.MinY) * 256 + (z << 4) + x;

    public ushort Get(ChunkPos pos, int x, int y, int z, ushort original)
    {
        ColumnEdits edits = For(pos);
        lock (edits.Gate)
            return edits.States.TryGetValue(Index(x, y, z), out ushort state) ? state : original;
    }

    public (long Revision, KeyValuePair<int, ushort>[] Changes) Snapshot(ChunkPos pos)
    {
        ColumnEdits edits = For(pos);
        lock (edits.Gate) return (edits.Revision, edits.States.ToArray());
    }

    public long Revision(ChunkPos pos)
    {
        ColumnEdits edits = For(pos);
        lock (edits.Gate) return edits.Revision;
    }

    public bool TrySet(ChunkPos pos, int x, int y, int z, ushort original,
        ushort state, out ushort previous)
    {
        ColumnEdits edits = For(pos);
        int index = Index(x, y, z);
        lock (edits.Gate)
        {
            bool wasEdited = edits.States.TryGetValue(index, out ushort prior);
            previous = wasEdited ? prior : original;
            if (previous == state) return false;

            if (state == original) edits.States.Remove(index);
            else edits.States[index] = state;
            try { Persist(pos, edits.States); }
            catch
            {
                if (wasEdited) edits.States[index] = prior;
                else edits.States.Remove(index);
                throw;
            }
            edits.Revision++;
            return true;
        }
    }

    private void Persist(ChunkPos pos, Dictionary<int, ushort> states)
    {
        if (_directory is null) return;
        string destination = PathFor(pos);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(pos.X);
                writer.Write(pos.Z);
                writer.Write(states.Count);
                foreach (var (index, state) in states.OrderBy(p => p.Key))
                {
                    writer.Write(index);
                    writer.Write(state);
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
