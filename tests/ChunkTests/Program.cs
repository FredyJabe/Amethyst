// Decodes the actual procedural C# chunk output, including protocol-777
// self-sized palettes, fluid counts, 384-high heightmaps, and light masks.
using System.Buffers.Binary;
using Amethyst;

var settings = new ServerOptions
{
    Seed = 87420913, BaseHeight = 70, SeaLevel = 63,
    HeightVariation = 36, TerrainScale = 1, Biome = "auto", ViewDistance = 2,
    SaveGeneratedChunks = false
};
var world = new TerrainWorld(settings);
var worldAgain = new TerrainWorld(settings);
var anotherSeed = new TerrainWorld(new ServerOptions
{
    Seed = settings.Seed + 1, BaseHeight = settings.BaseHeight,
    SeaLevel = settings.SeaLevel, HeightVariation = settings.HeightVariation,
    TerrainScale = settings.TerrainScale, Biome = "auto", ViewDistance = 2,
    SaveGeneratedChunks = false
});

var sampleCoords = new[]
{
    (0, 0), (15, 15), (16, 16), (-1, -1), (-16, 18), (-17, -23),
    (120, 80), (450, 900), (1024, -2048), (-5000, 9000)
};
foreach (var (x, z) in sampleCoords)
    Expect(world.SampleTerrain(x, z) == worldAgain.SampleTerrain(x, z), "Same seed must generate identical terrain");
Expect(sampleCoords.Any(c => world.SampleTerrain(c.Item1, c.Item2) !=
                             anotherSeed.SampleTerrain(c.Item1, c.Item2)), "Changing seed must change terrain");
Console.WriteLine("PASS: identical seed repeatability and different seed variation");

for (int z = -2; z <= 2; z++)
for (int x = -2; x <= 2; x++)
{
    Expect(world.GetBlock(x, 79, z) == TerrainBlocks.Stone, "Spawn stone pad missing");
    Expect(world.GetBlock(x, 78, z) != TerrainBlocks.Air, "Spawn pad should sit on terrain");
    Expect(world.GetBlock(x, 80, z) == TerrainBlocks.Air, "Spawn head space must be air");
}
Console.WriteLine("PASS: spawn pad has exactly the requested 5x5 stone top, supported by terrain");

var terrainSamples = new List<TerrainSample>();
for (int x = -3000; x <= 3000; x += 140)
for (int z = -3000; z <= 3000; z += 140)
    terrainSamples.Add(world.SampleTerrain(x, z));
Expect(terrainSamples.Max(v => v.GroundY) - terrainSamples.Min(v => v.GroundY) >= 15,
    "Terrain should include varied elevations");
Expect(terrainSamples.Select(v => v.BiomeId).Distinct().Count() >= 3,
    "Climate generator should contain at least three biomes");
Console.WriteLine("PASS: hills, elevation ranges and climate biome variation");
Expect(terrainSamples.Any(v => v.GroundY < settings.SeaLevel),
    "Seeded terrain should contain areas below sea level");
foreach (var pos in new[] { new ChunkPos(-1, -1), new ChunkPos(3, -5), new ChunkPos(20, 11) })
    Expect(world.SerializeSections(pos).SequenceEqual(worldAgain.SerializeSections(pos)),
        $"Chunk data must be independent of generation instance/order: {pos}");
Console.WriteLine("PASS: reproducible biome and block palettes across chunk generation instances");

var coords = new[]
{
    new ChunkPos(0, 0), new ChunkPos(-1, 0), new ChunkPos(0, -1),
    new ChunkPos(-1, -1), new ChunkPos(4, 4), new ChunkPos(-48, 35),
    new ChunkPos(60, -60)
};
foreach (ChunkPos pos in coords)
{
    var column = world.GetColumn(pos);
    byte[] serialized = world.SerializeSections(pos);
    var reader = new Cursor(serialized);
    for (int sectionId = 0; sectionId < ChunkColumn.SectionCount; sectionId++)
    {
        WorldSection expectedSection = column.Sections[sectionId];
        int nonAir = reader.ReadInt16();
        int fluids = reader.ReadInt16();
        int[] blocks = ReadContainer(reader, 4096, isBlock: true);
        int[] biomes = ReadContainer(reader, 64, isBlock: false);
        Expect(nonAir == blocks.Count(id => id != TerrainBlocks.Air), "Non-air count wrong");
        Expect(fluids == blocks.Count(id => id is TerrainBlocks.Water or TerrainBlocks.Lava), "Fluid count wrong");
        for (int i = 0; i < 4096; i++)
            Expect(blocks[i] == expectedSection.Blocks[i], $"Block palette mismatch in {pos}, section {sectionId}");
        for (int i = 0; i < 64; i++)
            Expect(biomes[i] == expectedSection.Biomes[i], $"Biome palette mismatch in {pos}, section {sectionId}");
    }
    Expect(reader.End, $"Section array has {reader.Remaining} trailing bytes in {pos}");
    Console.WriteLine($"PASS: 24 procedural sections in {pos} ({serialized.Length} bytes)");
}
Console.WriteLine("PASS: protocol-777 fixed-size palette decoding (varied blocks and biome samples)");

using (var stream = new SynchronizedMemoryStream())
using (var channel = new PacketChannel(stream))
{
    var view = new PlayerChunkView(world);
    var pose = new PlayerPose(TerrainWorld.SpawnX, TerrainWorld.SpawnY,
        TerrainWorld.SpawnZ, 0, 0, true);
    await view.UpdateAsync(channel, pose, CancellationToken.None);
    Packet[] first = await TakePacketsAsync(stream, awaitBatch: true);
    Expect(first.Length == 12, "Initial batch should contain center, start, 9 columns and finish");
    Expect(first[0].Id == 0x60 && first[1].Id == 0x0C && first[^1].Id == 0x0B,
        "Initial batch framing invalid");
    var positions = new HashSet<ChunkPos>();
    foreach (Packet packet in first[2..^1])
    {
        Expect(packet.Id == 0x2E, "Unexpected packet in chunk batch");
        positions.Add(VerifyChunkPacket(packet, world));
    }
    Expect(positions.Count == 9, "Initial 3x3 batch should contain nine unique chunks");
    // viewDistance=2 + one extra render-neighbor chunk = radius 3 (7x7).
    for (int rounds = 0; positions.Count < 49 && rounds < 15; rounds++)
    {
        await view.OnBatchReceivedAsync(channel, 16f, CancellationToken.None);
        Packet[] packets = await TakePacketsAsync(stream, awaitBatch: true);
        Expect(packets.Length >= 3 && packets[0].Id == 0x0C && packets[^1].Id == 0x0B,
            "Acknowledged chunk batch framing invalid");
        foreach (Packet packet in packets[1..^1])
            Expect(positions.Add(VerifyChunkPacket(packet, world)), "Duplicate transmitted chunk");
    }
    Expect(positions.Count == 49, "Expected full 7x7 radius with rendering halo");
    Console.WriteLine("PASS: chunk heightmaps, skylight masks, ACKed 7x7 chunk view");

    // After crossing X and negative-Z chunk borders, *only* chunks outside
    // the new render halo should be forgotten. In protocol 777 Forget Chunk
    // carries Z then X; reversing them silently unloads visible chunks.
    await view.UpdateAsync(channel,
        new PlayerPose(16.5, 80, -31.5, 0, 0, true), CancellationToken.None);
    Packet[] moved = await TakePacketsAsync(stream);
    int cacheCenterIndex = Array.FindIndex(moved, p => p.Id == 0x60);
    Expect(cacheCenterIndex > 0, "Old chunks must unload before new cache center");
    var expectedUnloads = positions.Where(c => c.X < -2 || c.X > 4 || c.Z < -5 || c.Z > 1)
        .ToHashSet();
    var receivedUnloads = new HashSet<ChunkPos>();
    for (int i = 0; i < cacheCenterIndex; i++)
    {
        Expect(moved[i].Id == 0x26, "Unexpected packet before cache center update");
        var reader = moved[i].Reader();
        int chunkZ = reader.ReadInt32();
        int chunkX = reader.ReadInt32();
        reader.RequireEnd();
        Expect(receivedUnloads.Add(new ChunkPos(chunkX, chunkZ)), "Duplicate unload packet");
    }
    Expect(receivedUnloads.SetEquals(expectedUnloads),
        "Forget Chunk must target the correct old columns (Z-then-X)");
    var centerReader = moved[cacheCenterIndex].Reader();
    Expect(centerReader.ReadVarInt() == 1 && centerReader.ReadVarInt() == -2,
        "View center must follow actual player chunk with correct signs");
    centerReader.RequireEnd();
    // A previously sent batch is still awaiting the client's ACK; movement
    // must not start a second overlapping batch. The ACK should instead
    // trigger sending columns around the NEW center.
    Expect(!moved[(cacheCenterIndex + 1)..].Any(p => p.Id == 0x2E),
        "Do not send a second chunk batch while awaiting an ACK");
    await view.OnBatchReceivedAsync(channel, 16f, CancellationToken.None);
    Packet[] afterAck = await TakePacketsAsync(stream, awaitBatch: true);
    Expect(afterAck.Length >= 3 && afterAck[0].Id == 0x0C && afterAck[^1].Id == 0x0B,
        "Client ACK should restart chunk streaming at new center");
    foreach (Packet packet in afterAck[1..^1])
    {
        Expect(packet.Id == 0x2E, "Expected only chunks within a batch");
        var chunkReader = packet.Reader();
        int cx = chunkReader.ReadInt32(), cz = chunkReader.ReadInt32();
        Expect(cx is >= -2 and <= 4 && cz is >= -5 and <= 1,
            "Stale chunk sent after movement instead of a new-view column");
    }
    Console.WriteLine("PASS: moving across positive/negative axes unloads correct coordinates before center");
}

// Regression: the initial position packet must precede the first chunk packet.
// The client may assign render meshes to its default (pre-teleport) camera
// otherwise, leaving solid and collidable chunks invisible.
using (var stream = new SynchronizedMemoryStream())
using (var channel = new PacketChannel(stream))
{
    var spawnSettings = new ServerOptions { GeneratorVersion = 1, ViewDistance = 2 };
    var spawnWorld = new TerrainWorld(spawnSettings);
    var player = new ConnectedPlayer(Guid.NewGuid(), "RenderCheck",
        Array.Empty<ProfileProperty>(), 123, channel, spawnWorld);
    await SpawnPackets.SendAsync(player, spawnWorld, spawnSettings,
        new WorldClock(spawnSettings), CancellationToken.None);
    Packet[] packets = await TakePacketsAsync(stream, awaitBatch: true);
    int positionIndex = Array.FindIndex(packets, p => p.Id == 0x49);
    int firstColumnIndex = Array.FindIndex(packets, p => p.Id == 0x2E);
    int centerIndex = Array.FindIndex(packets, p => p.Id == 0x60);
    Expect(positionIndex >= 0 && centerIndex > positionIndex &&
        firstColumnIndex > centerIndex, "Teleport and cache center must precede chunk data");
    Console.WriteLine("PASS: spawn position is established before first chunk batch");
}

using (var stream = new SynchronizedMemoryStream())
using (var channel = new PacketChannel(stream))
{
    var view = new PlayerChunkView(world);
    await view.UpdateAsync(channel, new PlayerPose(-0.5, 80, -0.5, 0, 0, true), CancellationToken.None);
    var first = (await TakePacketsAsync(stream, awaitBatch: true))[0].Reader();
    Expect(first.ReadVarInt() == -1 && first.ReadVarInt() == -1,
        "Chunk centers must floor correctly for negative world coordinates");
    first.RequireEnd();
    Console.WriteLine("PASS: negative-coordinate view center");
}

static ChunkPos VerifyChunkPacket(Packet packet, TerrainWorld world)
{
    var r = packet.Reader();
    int cx = r.ReadInt32(), cz = r.ReadInt32();
    var column = world.GetColumn(new ChunkPos(cx, cz));
    Expect(r.ReadVarInt() == 3, "Missing heightmaps");
    for (int k = 0; k < 3; k++)
    {
        Expect(r.ReadVarInt() is 1 or 4 or 5, "Unexpected heightmap type");
        Expect(r.ReadVarInt() == 37, "Heightmap must contain 37 longs");
        ulong[] heights = Enumerable.Range(0, 37).Select(_ => unchecked((ulong)r.ReadInt64())).ToArray();
        for (int i = 0; i < 256; i++)
        {
            int height = (int)((heights[i / 7] >> ((i % 7) * 9)) & 511);
            Expect(height == column.TopY[i] + 65, "Incorrect heightmap at index " + i);
        }
    }
    byte[] sections = r.ReadByteArray(2 * 1024 * 1024);
    Expect(sections.SequenceEqual(world.SerializeSections(new ChunkPos(cx, cz))),
        "Chunk's transmitted section bytes do not match generated sections");
    Expect(r.ReadVarInt() == 0, "Unexpected block entities");
    byte[] sky = r.ReadByteArray(4), block = r.ReadByteArray(4);
    byte[] emptySky = r.ReadByteArray(4), emptyBlock = r.ReadByteArray(4);
    int skyCount = Enumerable.Range(0, 26).Count(i => IsBitSet(sky, i));
    int blockCount = Enumerable.Range(0, 26).Count(i => IsBitSet(block, i));
    Expect(r.ReadVarInt() == skyCount, "Skylight mask/list mismatch");
    for (int layer = 0; layer < 26; layer++)
    {
        Expect(IsBitSet(sky, layer) != IsBitSet(emptySky, layer), "Sky layer must be lit or empty");
        Expect(IsBitSet(block, layer) != IsBitSet(emptyBlock, layer),
            "Block light layer must be lit or empty");
        if (IsBitSet(sky, layer))
        {
            byte[] light = r.ReadByteArray(2048);
            Expect(light.Length == 2048, "Skylight nibble array must have 2048 bytes");
        }
    }
    Expect(r.ReadVarInt() == blockCount, "Block light mask/list mismatch");
    for (int index = 0; index < blockCount; index++)
    {
        byte[] light = r.ReadByteArray(2048);
        Expect(light.Length == 2048, "Block light nibble array must have 2048 bytes");
    }
    r.RequireEnd();
    return new ChunkPos(cx, cz);
}

static bool IsBitSet(byte[] mask, int bit) =>
    bit / 8 < mask.Length && (mask[bit / 8] & (1 << (bit % 8))) != 0;

static int[] ReadContainer(Cursor reader, int capacity, bool isBlock)
{
    int bits = reader.ReadByte();
    var values = new int[capacity];
    if (bits == 0)
    {
        Array.Fill(values, reader.ReadVarInt());
        return values;
    }
    bool directPalette = isBlock ? bits > 8 : bits > 3;
    Expect(isBlock ? bits is >= 4 and <= 16 : bits is >= 1 and <= 7,
        "Palette bits outside protocol-777 limits");
    if (directPalette) Expect(bits == (isBlock ? 16 : 7), "Incorrect directPalette-palette width");
    int[]? palette = null;
    if (!directPalette)
    {
        int count = reader.ReadVarInt();
        Expect(count >= 1 && count <= (1 << bits), "Invalid indirect palette size");
        palette = Enumerable.Range(0, count).Select(_ => reader.ReadVarInt()).ToArray();
    }
    int perLong = 64 / bits, mask = (1 << bits) - 1;
    for (int i = 0; i < capacity; i += perLong)
    {
        ulong packed = reader.ReadUInt64();
        for (int j = 0; j < perLong && i + j < capacity; j++)
        {
            int index = (int)((packed >> (j * bits)) & (ulong)mask);
            if (directPalette) values[i + j] = index;
            else
            {
                Expect(index < palette!.Length, "Packed palette index out of range");
                values[i + j] = palette[index];
            }
        }
    }
    return values;
}

// Chunk batches are prepared asynchronously. Wait for the Finished packet
// instead of assuming UpdateAsync blocks while generating all 9+ columns.
static async Task<Packet[]> TakePacketsAsync(MemoryStream stream, bool awaitBatch = false)
{
    if (awaitBatch)
    {
        bool completed = false;
        for (int attempt = 0; attempt < 2400 && !completed; attempt++)
        {
            using var snapshot = new MemoryStream(stream.ToArray(), writable: false);
            try
            {
                int lastId = -1;
                while (snapshot.Position < snapshot.Length)
                {
                    Packet? msg = await Protocol.ReadPacketAsync(snapshot, CancellationToken.None);
                    if (!msg.HasValue) break;
                    lastId = msg.Value.Id;
                }
                completed = lastId == 0x0B; // Chunk Batch Finished
            }
            catch (EndOfStreamException) { } // concurrent frame still being written
            catch (InvalidDataException) { }
            if (!completed) await Task.Delay(50);
        }
        Expect(completed, "Timed out waiting for asynchronous chunk batch");
    }
    stream.Position = 0;
    var messages = new List<Packet>();
    while (stream.Position < stream.Length)
    {
        Packet? packet = await Protocol.ReadPacketAsync(stream, CancellationToken.None);
        Expect(packet.HasValue, "Truncated packet stream");
        messages.Add(packet!.Value);
    }
    stream.SetLength(0);
    stream.Position = 0;
    return messages.ToArray();
}

static void Expect(bool ok, string message)
{
    if (!ok) throw new InvalidDataException(message);
}

sealed class Cursor
{
    private readonly byte[] _buffer;
    private int _position;
    public Cursor(byte[] buffer) => _buffer = buffer;
    public int Remaining => _buffer.Length - _position;
    public bool End => Remaining == 0;
    public byte ReadByte() => Read(1)[0];
    public short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(Read(2));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(Read(8));
    public int ReadVarInt()
    {
        int number = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = ReadByte();
            number |= (b & 127) << (i * 7);
            if ((b & 128) == 0) return number;
        }
        throw new InvalidDataException("Invalid VarInt");
    }
    private ReadOnlySpan<byte> Read(int n)
    {
        if (n > Remaining) throw new InvalidDataException("Truncated chunk section");
        var data = _buffer.AsSpan(_position, n);
        _position += n;
        return data;
    }
}

// A chunk worker writes on a background thread. Snapshotting a normal
// MemoryStream concurrently is not safe; serialize writes and snapshots.
sealed class SynchronizedMemoryStream : MemoryStream
{
    private readonly object _sync = new();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) base.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
    public override byte[] ToArray() { lock (_sync) return base.ToArray(); }
}
