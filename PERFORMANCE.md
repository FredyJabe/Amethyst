# Amethyst — Chunk Performance Update (.NET 10, Java 26.3)

This release changes the **execution path**, not the seed-to-world algorithm. It retains
`generatorVersion: 7`, the existing YAML world fingerprint, `.mc26` chunk files,
chunk packet IDs, client ACK flow, online authentication and the interactive console.
The new C# runtime tests compare generated section bytes across worker counts and
cache sizes. **No speedup percentage is claimed until you run benchmarks locally.**

## Quick start

```bash
dotnet build src/Amethyst.csproj
dotnet run --project tests/PerformanceTests/PerformanceTests.csproj
dotnet run --project tests/PerformanceTests/PerformanceTests.csproj -- --bench
dotnet run --project tests/VisualTests/VisualTests.csproj
dotnet run --project tests/ChunkTests/ChunkTests.csproj
./run.sh
```

In the live server console, enter `perf` for stage timings, cache hits,
queue depth, worker utilization, GC collections and allocations. Set
`performanceReportSeconds: 60` to log a report every minute (0 disables
periodic reports). The counters are cumulative wall times, **not CPU time**,
so overlapping worker times should not be added to derive total throughput.

For real CPU profiles on Linux, consider Microsoft's `dotnet-counters` and
`dotnet-trace` tools. Example after installing them:

```bash
dotnet-counters monitor --process-id <PID> System.Runtime
dotnet-trace collect --process-id <PID>
```

## Settings in server.yaml

| YAML key | Default | What it controls |
|---|---:|---|
| `chunkWorkers` | 4 | Background chunk preparation workers |
| `maxQueuedChunks` | 1024 | Max outstanding shared chunk jobs |
| `maxCachedColumns` | 1024 | Generated chunk LRU capacity |
| `maxCachedLights` | 512 | Computed final light packet LRU capacity |
| `maxCachedLightSources` | 256 | Packed per-column light input LRU capacity |
| `maxCachedPackets` | 512 | Fully framed, unencrypted chunk packet LRU |
| `cachedTerrainSamples` | 32768 | Terrain/climate sample LRU |
| `cachedFeatureHeights` | 8192 | Density-based feature-height LRU |
| `asyncWorldSaves` | true | Queue new chunk saves on a separate writer |
| `saveQueueCapacity` | 128 | Limit queued unsaved immutable columns |
| `performanceReportSeconds` | 0 | Periodic diagnostics; 0 means manual only |

These options do **not** enter the persistent world identity fingerprint.
You may tune them while keeping `seed`, `generatorVersion`, and `worldPath`.

On an 8-core PC, start with 3–4 workers and review `perf`. On a 4-core CPU,
start with 2 workers. More workers may raise disk and GC pressure rather than
improve throughput. Cache settings are hard limits after in-flight creations
complete; brief overshoot during concurrent creation is possible.

## Pipeline changes

1. **Request prioritization**: Chebyshev distance to the player's center,
   FIFO within a priority. Shared requests for one coordinate coalesce;
   canceled clients detach without canceling other clients' needs.
2. **Packet preparation**: background worker builds the chunk, lighting and
   framed plaintext packet once; the socket thread only sends cached frames.
   Connection-specific AES/CFB8 is still applied separately at send time.
3. **Lighting inputs**: each immutable column caches a packed 4-bit volume of
   block opacity, direct sky and emission; target chunks still perform their
   original lateral light propagation using a 15-block halo.
4. **Lighting scratch**: a large three-array working volume uses `ArrayPool`.
   Fully opaque and unlit sections bypass repeated state classification.
5. **Generation fast path**: 4x8x4 density cells with strictly same-sign
   corners skip per-voxel interpolation. Mixed cells retain the previous
   interpolation arithmetic. Biome samples reuse the exact column samples;
   feature-height and terrain samples have bounded memoization.
6. **Bounded memory**: LRU eviction replaces unbounded chunk and light
   dictionaries. Immutable snapshots stay valid for in-flight sends.
7. **World persistence**: the original compressed `.mc26` format remains;
   a bounded channel batches available write requests and drains on shutdown.

## Compatibility, safety, and limits

- The v7 generation formulas, feature placement, block-state IDs and biome
  selection were not deliberately changed. The C# `PerformanceTests` compare
  serialized chunks across concurrency/load order and verify that the cell
  shortcut agrees with the original density interpolation.
- Back up your world before upgrading. The unchanged world format should
  remain readable, but **actual cross-version bit-for-bit equivalence cannot
  be certified without executing both builds on the same seed**.
- A source-only patch updates only source and tests. Keep your own `server.yaml`,
  `ops.yaml`, `run.sh`, `time.yaml`, saved worlds and existing files intact.
- This release does **not** change lighting dynamically after block edits;
  block placement and incremental lighting are future gameplay features.
- Storage is asynchronous and queued, but still uses one `.mc26` GZip file per
  chunk. A region-file layout and world-edit journal require a separate migration.
- A fully fresh 21x21 view still costs work: profiling is important before
  asserting a speed improvement. Performance tuning is not a protocol fix.

## Suggested acceptance checklist

1. Run `dotnet build` and all three .NET test projects above.
2. Copy the world directory as a backup, connect and fly across both positive
   and negative chunk coordinates; verify visibility and chunk loading.
3. Connect two clients, move them toward the same unloaded area, and watch
   `perf` for shared queue work and packet cache hits.
4. Restart and confirm that generated chunks reload from the existing world
   directory, and the day/night clock and `ops.yaml` remain unchanged.
5. Compare time-to-visibility, `perf` stage timings, memory and GC under the
   same view distance/seed before and after this update.
