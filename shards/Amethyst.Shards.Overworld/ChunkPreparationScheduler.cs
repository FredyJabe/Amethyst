using System.Diagnostics;

namespace Amethyst;

/// <summary>
/// Shared priority queue for expensive chunk preparation. Coalesces requests for
/// identical chunk coordinates and limits both active workers and queued items.
/// A player's cancellation detaches its wait without canceling another player's
/// request for the same chunk. When nobody needs a queued job, workers skip it.
/// </summary>
public sealed class ChunkPreparationScheduler : IAsyncDisposable
{
    private sealed class Job(ChunkPos pos, long enqueuedAt)
    {
        public ChunkPos Position { get; } = pos;
        public long EnqueuedAt { get; } = enqueuedAt;
        public int Subscribers;
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly TerrainWorld _world;
    private readonly ChunkPerformance _metrics;
    private readonly object _gate = new();
    private readonly Dictionary<ChunkPos, Job> _pending = new();
    private readonly PriorityQueue<Job, (int Distance, long Serial)> _queue = new();
    private readonly SemaphoreSlim _workSignal = new(0);
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private long _serial;
    public int WorkerCount => _workers.Length;

    public ChunkPreparationScheduler(TerrainWorld world, int workers, int maximumOutstanding)
    {
        if (workers < 1 || maximumOutstanding < workers)
            throw new ArgumentOutOfRangeException(nameof(maximumOutstanding));
        _world = world;
        _metrics = world.Performance;
        _slots = new SemaphoreSlim(maximumOutstanding, maximumOutstanding);
        _workers = Enumerable.Range(0, workers).Select(_ => Task.Run(WorkerLoopAsync)).ToArray();
    }

    public async Task<byte[]> PrepareAsync(ChunkPos pos, int priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Job? found;
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested) throw new ObjectDisposedException(nameof(ChunkPreparationScheduler));
            if (_pending.TryGetValue(pos, out found))
                found.Subscribers++;
        }
        if (found is not null) return await AwaitSubscriberAsync(found, ct).ConfigureAwait(false);

        // Backpressure happens on the requesting task, never on the socket reader.
        using var linkedWait = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _slots.WaitAsync(linkedWait.Token).ConfigureAwait(false);
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested)
            {
                _slots.Release();
                throw new ObjectDisposedException(nameof(ChunkPreparationScheduler));
            }
            if (_pending.TryGetValue(pos, out found))
            {
                found.Subscribers++;
                _slots.Release();
            }
            else
            {
                found = new Job(pos, ChunkPerformance.Start()) { Subscribers = 1 };
                _pending.Add(pos, found);
                _queue.Enqueue(found, (Math.Max(0, priority), ++_serial));
                _metrics.QueueChanged(_queue.Count);
                _workSignal.Release();
            }
        }
        return await AwaitSubscriberAsync(found!, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> AwaitSubscriberAsync(Job job, CancellationToken ct)
    {
        try { return await job.Completion.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            lock (_gate) job.Subscribers--;
        }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (true)
            {
                await _workSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Job? job;
                lock (_gate)
                {
                    if (!_queue.TryDequeue(out job, out _)) continue;
                    _metrics.QueueChanged(_queue.Count);
                }
                if (job is null) continue;
                _metrics.Record(ChunkStage.QueueWait, job.EnqueuedAt);
                _metrics.WorkerEntered();
                try
                {
                    bool wanted;
                    lock (_gate) wanted = job.Subscribers != 0;
                    if (wanted)
                    {
                        // The blocking generation/storage/lighting work is isolated
                        // on bounded workers; client receive loops stay responsive.
                        byte[] frame = _world.PrepareChunkFrame(job.Position);
                        job.Completion.TrySetResult(frame);
                    }
                    else job.Completion.TrySetCanceled();
                }
                catch (Exception exception)
                {
                    job.Completion.TrySetException(exception);
                }
                finally
                {
                    _metrics.WorkerExited();
                    lock (_gate)
                    {
                        if (_pending.TryGetValue(job.Position, out var existing) && ReferenceEquals(existing, job))
                            _pending.Remove(job.Position);
                    }
                    _slots.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        lock (_gate)
        {
            foreach (Job job in _pending.Values) job.Completion.TrySetCanceled();
        }
        try { await Task.WhenAll(_workers).ConfigureAwait(false); }
        finally
        {
            _workSignal.Dispose();
            _slots.Dispose();
            _shutdown.Dispose();
        }
    }
}
