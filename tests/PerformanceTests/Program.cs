using Amethyst;
using Amethyst.Generation.Terrain;
using System.Diagnostics;
using System.Security.Cryptography;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

const long TestSeed = 87420913;
var positions = new[]
{
    new ChunkPos(0, 0), new ChunkPos(-1, 0), new ChunkPos(0, -1),
    new ChunkPos(-1, -1), new ChunkPos(7, 3), new ChunkPos(-7, 3),
    new ChunkPos(3, -11), new ChunkPos(-3, -11), new ChunkPos(19, 23),
    new ChunkPos(-20, 14)
};

static ServerOptions Settings(int workers, int cacheSize = 128) => new()
{
    Seed = TestSeed, GeneratorVersion = 7, SaveGeneratedChunks = false,
    ChunkWorkers = workers, MaxQueuedChunks = 32,
    MaxCachedColumns = cacheSize, MaxCachedLights = 32,
    MaxCachedPackets = 32, CachedTerrainSamples = 2048,
    CachedFeatureHeights = 1024, ViewDistance = 2
};

static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

// A known-order, one-worker, low-cache reference for the current version-7
// generator. Compare serialized section bytes, not just block counts.
var reference = new Dictionary<ChunkPos, string>();
var sequentialWorld = new TerrainWorld(Settings(1));
foreach (ChunkPos pos in positions)
    reference[pos] = Hash(sequentialWorld.SerializeSections(pos));
await sequentialWorld.DisposeAsync();
Console.WriteLine("PASS: reference v7 world signatures generated");

var world = new TerrainWorld(Settings(4, 32));
try
{
    // Overlap and out-of-order work should produce exactly identical source
    // blocks and biomes, regardless of worker count or cache eviction.
    ChunkPos[] requestOrder = positions.Reverse().Concat(positions.Take(4)).ToArray();
    Task<byte[]>[] tasks = requestOrder.Select((pos, i) => world.Scheduler.PrepareAsync(
        pos, priority: i % 4, CancellationToken.None)).ToArray();
    byte[][] frames = await Task.WhenAll(tasks);
    for (int i = 0; i < requestOrder.Length; i++)
    {
        ChunkPos pos = requestOrder[i];
        Require(Hash(world.SerializeSections(pos)) == reference[pos],
            $"v7 chunk changed with concurrent work/cache eviction: {pos}");
        Require(frames[i].SequenceEqual(world.PrepareChunkFrame(pos)),
            "Prepared packet must be cached and byte-identical");
        using var stream = new MemoryStream(frames[i], writable: false);
        Packet? pkt = await Protocol.ReadPacketAsync(stream, CancellationToken.None);
        Require(pkt.HasValue && pkt.Value.Id == 0x2E && stream.Position == stream.Length,
            "Cached frame must be one valid Minecraft chunk packet");
        var reader = pkt!.Value.Reader();
        Require(reader.ReadInt32() == pos.X && reader.ReadInt32() == pos.Z,
            "Prepared packet belongs to the wrong chunk");
    }
    Require(world.CachedColumns <= 32 && world.CachedLights <= 32 && world.CachedPackets <= 32,
        "Bounded world caches exceeded configured capacity after work completion");
    Require(world.Performance.Stage(ChunkStage.Encode).Count > 0,
        "Chunk encoding performance counter not updated");
    Console.WriteLine("PASS: same v7 sections across 1 vs 4 workers, reversed order, duplicate requests and LRU eviction");

    // Mathematical guarantee used by the fast density cell classifier.
    var density = new DensitySampler(new TerrainGenerator(Settings(1)), Settings(1));
    foreach (ChunkPos pos in new[] { new ChunkPos(0, 0), new ChunkPos(-5, 2), new ChunkPos(20, -17) })
    {
        DensityGrid grid = density.BuildGrid(pos);
        byte[] signs = grid.ClassifyCells();
        for (int y = ChunkColumn.MinY; y <= ChunkColumn.MaxY; y++)
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int cell = (((y - ChunkColumn.MinY) / 8) * 4 + z / 4) * 4 + x / 4;
            byte sign = signs[cell];
            if (sign == 0) continue;
            bool exact = grid.Interpolate(x, y, z) > 0;
            Require((sign == 1) == exact, $"Density fast path altered solid/air at {pos}, {x},{y},{z}");
        }
    }
    Console.WriteLine("PASS: classified density cells agree with original interpolation at every block");
}
finally { await world.DisposeAsync(); }

// The light-source fast path caches precisely the old vertical data, not a
// new lighting model. Exercise opacity and emission, including invalidation.
var cube = new WorldSection(0);
for (int y = 0; y < 16; y++)
for (int z = 0; z < 16; z++)
for (int x = 0; x < 16; x++) cube.SetBlock(x, y, z, TerrainBlocks.Stone);
Require(cube.FullyOpaqueAndUnlit, "All-stone section should use fast lighting path");
cube.SetBlock(2, 2, 2, TerrainBlocks.Lava);
Require(!cube.FullyOpaqueAndUnlit, "Lava must invalidate opaque-unlit shortcut");
var lightTest = new ChunkColumn(new ChunkPos(0, 0), 0);
lightTest.SetBlock(3, 80, 4, TerrainBlocks.OakLeaves);
lightTest.SetBlock(3, 75, 4, TerrainBlocks.Water);
lightTest.SetBlock(3, 73, 4, TerrainBlocks.Stone);
lightTest.SetBlock(3, 70, 4, TerrainBlocks.Lava);
var source = ChunkLightEngine.PrepareSource(lightTest);
byte expectedSky = 15;
for (int y = ChunkColumn.MaxY; y >= ChunkColumn.MinY; y--)
{
    ushort state = lightTest.GetBlock(3, y, 4);
    byte opacity = state == TerrainBlocks.OakLeaves ? (byte)1 :
        state == TerrainBlocks.Water ? (byte)2 :
        state == TerrainBlocks.Air ? (byte)0 : (byte)15;
    expectedSky = opacity == 15 ? (byte)0 : (byte)Math.Max(0, expectedSky - opacity);
    Require(source.OpacityAt(3, y - ChunkColumn.MinY, 4) == opacity,
        "Packed light opacity mismatch");
    Require(source.SkyAt(3, y - ChunkColumn.MinY, 4) == expectedSky,
        "Packed vertical skylight mismatch");
    Require(source.EmissionAt(3, y - ChunkColumn.MinY, 4) ==
        (state == TerrainBlocks.Lava ? 15 : 0), "Packed lava emission mismatch");
}
Console.WriteLine("PASS: packed light sources preserve old direct-sky and emission rules");

// New background storage must still emit the legacy .mc26 wire-independent
// chunk files and world.yaml manifest after clean shutdown.
string directory = Path.Combine(Path.GetTempPath(), "mc26-perf-" + Guid.NewGuid().ToString("N"));
try
{
    ServerOptions persisted = Settings(2);
    persisted.SaveGeneratedChunks = true;
    persisted.AsyncWorldSaves = true;
    persisted.SaveQueueCapacity = 2;
    persisted.WorldPath = directory;
    ChunkPos pos = new(-4, 7);
    var generating = new TerrainWorld(persisted);
    byte[] expected = generating.SerializeSections(pos);
    await generating.DisposeAsync();
    Require(File.Exists(Path.Combine(directory, "world.yaml")), "Missing YAML world manifest");
    Require(File.Exists(Path.Combine(directory, "chunks", "-4.7.mc26")),
        "Async saver did not drain queued chunk on shutdown");
    var loading = new TerrainWorld(persisted);
    Require(expected.SequenceEqual(loading.SerializeSections(pos)),
        "Reload changed saved-world blocks or biomes");
    await loading.DisposeAsync();
    Console.WriteLine("PASS: background save drain and legacy chunk format round-trip");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

if (args.Contains("--bench"))
{
    ServerOptions options = Settings(Math.Min(Environment.ProcessorCount, 4));
    var benchWorld = new TerrainWorld(options);
    ChunkPos[] work = Enumerable.Range(0, 24).Select(i => new ChunkPos(i * 3 - 40, i * 2 - 24)).ToArray();
    var timer = Stopwatch.StartNew();
    await Task.WhenAll(work.Select((pos, i) => benchWorld.Scheduler.PrepareAsync(pos, i, CancellationToken.None)));
    timer.Stop();
    Console.WriteLine($"[benchmark] {work.Length} cold chunks, {options.ChunkWorkers} workers: {timer.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine(benchWorld.Performance.Report(options.ChunkWorkers));
    await benchWorld.DisposeAsync();
}
Console.WriteLine("PASS: chunk performance and determinism suite");
