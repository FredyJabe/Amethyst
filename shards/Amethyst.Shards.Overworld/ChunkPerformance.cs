using System.Diagnostics;
using System.Text;

namespace Amethyst;

public enum ChunkStage
{
    Generate, DensityGrid, VolumeFill, Biomes, Decoration, Heightmaps,
    DiskLoad, DiskSave, LightSources, Lighting, Encode, QueueWait, Send
}

/// <summary>Low-allocation, thread-safe cumulative measurements. Times are wall-clock latency,
/// not CPU time; overlapping workers may run concurrently.</summary>
public sealed class ChunkPerformance
{
    private readonly long[] _counts = new long[Enum.GetValues<ChunkStage>().Length];
    private readonly long[] _ticks = new long[Enum.GetValues<ChunkStage>().Length];
    private readonly long[] _maxTicks = new long[Enum.GetValues<ChunkStage>().Length];
    private long _columnHits, _columnMisses, _lightHits, _lightMisses, _packetHits, _packetMisses;
    private long _sourceHits, _sourceMisses;
    private long _queueLength, _queuePeak;
    private int _workersBusy;
    private readonly long _allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);

    public static long Start() => Stopwatch.GetTimestamp();
    public void Record(ChunkStage stage, long start)
    {
        long elapsed = Math.Max(0, Stopwatch.GetTimestamp() - start);
        int i = (int)stage;
        Interlocked.Increment(ref _counts[i]);
        Interlocked.Add(ref _ticks[i], elapsed);
        long old;
        while (elapsed > (old = Volatile.Read(ref _maxTicks[i])) &&
               Interlocked.CompareExchange(ref _maxTicks[i], elapsed, old) != old) { }
    }
    public void CacheHit(string cache, bool hit)
    {
        switch (cache)
        {
            case "column": if (hit) Interlocked.Increment(ref _columnHits); else Interlocked.Increment(ref _columnMisses); break;
            case "light": if (hit) Interlocked.Increment(ref _lightHits); else Interlocked.Increment(ref _lightMisses); break;
            case "packet": if (hit) Interlocked.Increment(ref _packetHits); else Interlocked.Increment(ref _packetMisses); break;
            case "source": if (hit) Interlocked.Increment(ref _sourceHits); else Interlocked.Increment(ref _sourceMisses); break;
        }
    }
    public void QueueChanged(int length)
    {
        Volatile.Write(ref _queueLength, length);
        long old;
        while (length > (old = Volatile.Read(ref _queuePeak)) &&
               Interlocked.CompareExchange(ref _queuePeak, length, old) != old) { }
    }
    public void WorkerEntered() => Interlocked.Increment(ref _workersBusy);
    public void WorkerExited() => Interlocked.Decrement(ref _workersBusy);

    public (long Count, double MeanMs, double MaxMs) Stage(ChunkStage stage)
    {
        int i = (int)stage;
        long n = Interlocked.Read(ref _counts[i]);
        double ticks = Interlocked.Read(ref _ticks[i]);
        return (n, n == 0 ? 0 : ticks * 1000 / Stopwatch.Frequency / n,
            Interlocked.Read(ref _maxTicks[i]) * 1000d / Stopwatch.Frequency);
    }
    public long Queued => Interlocked.Read(ref _queueLength);
    public long PeakQueued => Interlocked.Read(ref _queuePeak);
    public int WorkersBusy => Volatile.Read(ref _workersBusy);

    public string Report(int workers)
    {
        var b = new StringBuilder();
        b.AppendLine("[perf] Chunk pipeline (cumulative wall time; use dotnet-counters for CPU/GC):");
        foreach (ChunkStage stage in Enum.GetValues<ChunkStage>())
        {
            var (n, avg, max) = Stage(stage);
            b.AppendLine($"[perf] {stage,-10}: {n,7} calls, avg {avg,8:F2} ms, max {max,8:F2} ms");
        }
        AppendCache("column", Interlocked.Read(ref _columnHits), Interlocked.Read(ref _columnMisses));
        AppendCache("light", Interlocked.Read(ref _lightHits), Interlocked.Read(ref _lightMisses));
        AppendCache("source", Interlocked.Read(ref _sourceHits), Interlocked.Read(ref _sourceMisses));
        AppendCache("packet", Interlocked.Read(ref _packetHits), Interlocked.Read(ref _packetMisses));
        b.AppendLine($"[perf] queued={Queued} peak={PeakQueued} workers={WorkersBusy}/{workers} " +
                     $"managedHeap={GC.GetTotalMemory(false) / 1048576d:F1} MiB");
        b.AppendLine($"[perf] allocatedSinceStart=" +
            $"{(GC.GetTotalAllocatedBytes(false) - _allocatedAtStart) / 1048576d:F1} MiB, " +
            $"GC gen0/gen1/gen2={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
        return b.ToString().TrimEnd();

        void AppendCache(string name, long hits, long misses) =>
            b.AppendLine($"[perf] {name,-10} cache: {hits} hits / {misses} misses " +
                         $"({(hits + misses == 0 ? 0 : (100d * hits / (hits + misses))):F1}% hit)");
    }
}

/// <summary>Bounded, exact LRU cache for immutable objects. Expensive factories run outside the
/// lock. Lazy creation coalesces overlapping requests; in-flight entries are never evicted.</summary>
public sealed class BoundedLazyCache<TKey, TValue> where TKey : notnull
{
    private sealed class Entry(TKey key, Func<TValue> factory)
    {
        public TKey Key { get; } = key;
        public Lazy<TValue> Value { get; } = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);
    }
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _order = new();
    private readonly object _gate = new();
    public BoundedLazyCache(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }
    public int Count { get { lock (_gate) return _entries.Count; } }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, out bool hit)
    {
        LinkedListNode<Entry> node;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out node!))
            {
                hit = true;
                _order.Remove(node);
                _order.AddFirst(node);
            }
            else
            {
                hit = false;
                node = new LinkedListNode<Entry>(new Entry(key, () => factory(key)));
                _order.AddFirst(node);
                _entries.Add(key, node);
            }
            TrimLocked();
        }
        try { return node.Value.Value.Value; }
        catch
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var existing) && ReferenceEquals(existing, node))
                { _entries.Remove(key); _order.Remove(node); }
            }
            throw;
        }
        finally { lock (_gate) TrimLocked(); }
    }
    private void TrimLocked()
    {
        var cursor = _order.Last;
        while (_entries.Count > _capacity && cursor is not null)
        {
            var previous = cursor.Previous;
            if (cursor.Value.Value.IsValueCreated)
            {
                _entries.Remove(cursor.Value.Key);
                _order.Remove(cursor);
            }
            cursor = previous;
        }
    }
}
