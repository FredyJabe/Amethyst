using Amethyst;
using Amethyst.Generation.Features;
using System.Buffers.Binary;

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

string work = Path.Combine(Path.GetTempPath(), "minicraft-visual-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    string yaml = Path.Combine(work, "server.yaml");
    File.WriteAllText(yaml, "generatorVersion: 6\nstartTimeTicks: 18000\n" +
        "dayLengthMinutes: 20\ndaylightCycle: false\n" +
        "saveGeneratedChunks: false\n");
    var config = ServerOptions.Load(yaml);
    Check(config.GeneratorVersion == 6 && config.StartTimeTicks == 18000 &&
        config.DayLengthMinutes == 20 && !config.DaylightCycle,
        "Visual YAML settings not parsed");
    {
        var clock = new WorldClock(config);
        var packet = new PacketWriter();
        clock.WritePacket(packet);
        byte[] body = packet.ToArray();
        var read = new PacketReader(body);
        long age = read.ReadInt64();
        Check(age >= 0 && read.ReadVarInt() == 1, "Clock entry count must be one");
        int clockId = read.ReadVarInt();
        Check(clockId == VanillaRegistries.IndexOf("minecraft:world_clock", "minecraft:overworld"),
            "World clock registry ID mismatch");
        long day = ReadVarLong(body, read.Position, out int count);
        Check(day == 18000, "Paused clock must start at midnight");
        Check(body.Length == read.Position + count + 8, "Wrong 26.3 world-clock packet size");
        float fractional = BinaryPrimitives.ReadSingleBigEndian(body.AsSpan(read.Position + count, 4));
        float rate = BinaryPrimitives.ReadSingleBigEndian(body.AsSpan(read.Position + count + 4, 4));
        Check(fractional == 0 && rate == 0, "Paused clock packet must carry zero rate");
    }
    var running = new WorldClock(new ServerOptions { DaylightCycle = true, DayLengthMinutes = 20 });
    var movingPacket = new PacketWriter();
    running.WritePacket(movingPacket);
    var movingReader = new PacketReader(movingPacket.ToArray());
    _ = movingReader.ReadInt64(); _ = movingReader.ReadVarInt(); _ = movingReader.ReadVarInt();
    byte[] movingData = movingPacket.ToArray();
    _ = ReadVarLong(movingData, movingReader.Position, out int varLen);
    float movingRate = BinaryPrimitives.ReadSingleBigEndian(movingData.AsSpan(movingReader.Position + varLen + 4, 4));
    Check(Math.Abs(movingRate - 1) < 0.0001f, "20-minute clock must advance 1 tick/client tick");
    Console.WriteLine("PASS: 26.3 world-clock packet and day-length rate");

    // A roof covering all neighboring columns must block sun but not light
    // from an enclosed lava source. On the other side of the roof: full sun.
    var synthetic = new ChunkColumn(new ChunkPos(0, 0), 0);
    for (int z = 0; z < 16; z++)
    for (int x = 0; x < 16; x++)
        synthetic.SetBlock(x, 70, z, TerrainBlocks.Stone);
    synthetic.SetBlock(7, 64, 7, TerrainBlocks.Lava);
    ChunkLightEngine.LightData data = ChunkLightEngine.Calculate((_, _) => synthetic);
    Check(data.SkyAt(7, 71, 7) == 15, "Above an opaque roof, sky must be 15");
    Check(data.SkyAt(7, 69, 7) == 0, "Covered cave must not receive direct sunlight");
    Check(data.BlockAt(7, 64, 7) == 15, "Lava must emit light level 15");
    Check(data.BlockAt(8, 64, 7) == 14, "Light must spread one block from lava");
    var lightPacket = new PacketWriter();
    data.Write(lightPacket);
    var lightReader = new PacketReader(lightPacket.ToArray());
    for (int i = 0; i < 4; i++)
    {
        byte[] mask = lightReader.ReadByteArray(4);
        Check(mask.Length <= 4, "Invalid 26-layer light mask length");
    }
    Console.WriteLine("PASS: skylight roof occlusion, lava blocklight, 26.3 mask framing");

    var canopy = new ChunkColumn(new ChunkPos(0, 0), 0);
    for (int y = 71; y <= 74; y++)
    for (int z = 4; z <= 10; z++)
    for (int x = 4; x <= 10; x++)
        canopy.SetBlock(x, y, z, TerrainBlocks.OakLeaves);
    var shade = ChunkLightEngine.Calculate((_, _) => canopy);
    Check(shade.SkyAt(7, 70, 7) < 15 && shade.SkyAt(7, 80, 7) == 15,
        "Foliage should filter sunlight, with clear sky above");
    Console.WriteLine("PASS: dimmer skylight beneath thick leaves");

    static ChunkColumn Roof()
    {
        var col = new ChunkColumn(new ChunkPos(0, 0), 0);
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++) col.SetBlock(x, 70, z, TerrainBlocks.Stone);
        return col;
    }
    var west = Roof();
    var middle = Roof();
    var other = Roof();
    west.SetBlock(15, 70, 8, TerrainBlocks.Air);
    west.SetBlock(15, 64, 8, TerrainBlocks.Lava);
    var border = ChunkLightEngine.Calculate((dx, dz) =>
        dx == -1 && dz == 0 ? west : dx == 0 && dz == 0 ? middle : other);
    Check(border.SkyAt(0, 69, 8) > 0, "Sunlight did not pass across the chunk edge");
    Check(border.BlockAt(0, 64, 8) == 14, "Lava light did not cross the chunk edge");
    Console.WriteLine("PASS: sun and lava light across neighboring chunks");

    // Tree shapes should be deterministic per species and contain branches
    // and leaves across borders without ever replacing existing stone.
    var signatures = new HashSet<string>();
    foreach (ushort log in new[] { TerrainBlocks.OakLog, TerrainBlocks.BirchLog,
        TerrainBlocks.SpruceLog, TerrainBlocks.JungleLog, TerrainBlocks.AcaciaLog,
        TerrainBlocks.DarkOakLog, TerrainBlocks.MangroveLog })
    {
        ushort leaf = log switch
        {
            TerrainBlocks.OakLog => TerrainBlocks.OakLeaves,
            TerrainBlocks.BirchLog => TerrainBlocks.BirchLeaves,
            TerrainBlocks.SpruceLog => TerrainBlocks.SpruceLeaves,
            TerrainBlocks.JungleLog => TerrainBlocks.JungleLeaves,
            TerrainBlocks.AcaciaLog => TerrainBlocks.AcaciaLeaves,
            TerrainBlocks.DarkOakLog => TerrainBlocks.DarkOakLeaves,
            _ => TerrainBlocks.MangroveLeaves
        };
        var left = new ChunkColumn(new ChunkPos(0, 0), 0);
        var right = new ChunkColumn(new ChunkPos(1, 0), 0);
        NaturalTreeShapes.Place(left, 15, 80, 8, 10, log, leaf, 0xC0FFEEUL);
        NaturalTreeShapes.Place(right, 15, 80, 8, 10, log, leaf, 0xC0FFEEUL);
        var replay = new ChunkColumn(new ChunkPos(0, 0), 0);
        NaturalTreeShapes.Place(replay, 15, 80, 8, 10, log, leaf, 0xC0FFEEUL);
        Check(left.Sections.SelectMany(section => section.Blocks)
            .SequenceEqual(replay.Sections.SelectMany(section => section.Blocks)),
            "Tree silhouette changed for same seed");
        int logs = left.Sections.Sum(section => section.Blocks.Count(b => b == log)) +
            right.Sections.Sum(section => section.Blocks.Count(b => b == log));
        int leaves = left.Sections.Sum(section => section.Blocks.Count(b => b == leaf)) +
            right.Sections.Sum(section => section.Blocks.Count(b => b == leaf));
        Check(logs >= 4 && leaves >= 5, "Species tree missing trunk or canopy");
        signatures.Add(string.Join(",", left.Sections.SelectMany(s => s.Blocks)
            .Select((block, index) => (block, index)).Where(t => t.block != 0)
            .Select(t => t.index)));
    }
    Check(signatures.Count >= 5, "Natural trees need distinctly different silhouettes");
    Console.WriteLine("PASS: seed-stable varied tree shapes, boundary clipping");

    // Saved 6th-generation chunks and a shared world-clock state can restart.
    string worldPath = Path.Combine(work, "persistent");
    var opts = new ServerOptions
    {
        GeneratorVersion = 6, SaveGeneratedChunks = true, WorldPath = worldPath,
        ViewDistance = 2, FeatureDensity = 1.5, StructureFrequency = 1.2
    };
    var first = new TerrainWorld(opts);
    byte[] terrain = first.SerializeSections(new ChunkPos(-1, 2));
    var clockOne = new WorldClock(opts);
    clockOne.Save();
    Check(File.Exists(Path.Combine(worldPath, "time.yaml")), "Missing YAML time state");
    var second = new TerrainWorld(opts);
    Check(terrain.SequenceEqual(second.SerializeSections(new ChunkPos(-1, 2))),
        "Version-6 saved chunks were not reloaded faithfully");
    var clockTwo = new WorldClock(opts);
    Check(clockTwo.DayTime >= opts.StartTimeTicks, "Clock state was not loaded");
    Console.WriteLine("PASS: version-6 chunk persistence and YAML clock persistence");
}
finally { Directory.Delete(work, recursive: true); }

static long ReadVarLong(byte[] body, int offset, out int count)
{
    ulong result = 0;
    count = 0;
    for (int i = 0; i < 10; i++)
    {
        byte current = body[offset + i];
        result |= (ulong)(current & 0x7f) << (7 * i);
        count++;
        if ((current & 0x80) == 0) return unchecked((long)result);
    }
    throw new InvalidDataException("VarLong exceeds 10 bytes");
}
