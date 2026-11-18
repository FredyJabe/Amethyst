using Amethyst;

static void Check(bool ok, string why)
{
    if (!ok) throw new InvalidOperationException(why);
}

string directory = Path.Combine(Path.GetTempPath(),
    "minicraft-phase5-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string configPath = Path.Combine(directory, "server.yaml");
    File.WriteAllText(configPath,
        "seed: -895123456\ngeneratorVersion: 5\n" +
        "featureDensity: 1.5\noreFrequency: 1.25\n" +
        "saveGeneratedChunks: false\nworldPath: tempworld\n");
    var yaml = ServerOptions.Load(configPath);
    Check(yaml.Seed == -895123456 && yaml.GeneratorVersion == 5 &&
        yaml.FeatureDensity == 1.5 && yaml.OreFrequency == 1.25 &&
        !yaml.SaveGeneratedChunks && yaml.WorldPath == "tempworld",
        "Phase-5 YAML configuration was not deserialized");
    File.WriteAllText(configPath, "generatorVersion: 5\noreFrequency: 3\n");
    try { _ = ServerOptions.Load(configPath); throw new Exception("Invalid frequency accepted"); }
    catch (ArgumentOutOfRangeException) { }
    Console.WriteLine("PASS: version-5 YAML options and validation");

    var baseline = new ServerOptions
    {
        Seed = 87420913, GeneratorVersion = 5, Biome = "minecraft:forest",
        FeatureDensity = 2, OreFrequency = 2, SaveGeneratedChunks = false, ViewDistance = 2
    };
    var first = new TerrainWorld(baseline);
    var second = new TerrainWorld(baseline);
    ChunkPos left = new(4, 3), right = new(5, 3);
    ChunkColumn aLeft = first.GetColumn(left);
    ChunkColumn aRight = first.GetColumn(right);
    _ = second.GetColumn(right);
    _ = second.GetColumn(left);
    Check(first.SerializeSections(left).SequenceEqual(second.SerializeSections(left)) &&
        first.SerializeSections(right).SequenceEqual(second.SerializeSections(right)),
        "Load order changed seeded cross-border features");

    int oreBlocks = 0, plantBlocks = 0, leafBlocks = 0;
    foreach (var column in new[] { aLeft, aRight })
    {
        for (int s = 0; s < ChunkColumn.SectionCount; s++)
        {
            var section = column.Sections[s];
            Check(section.NonAir == section.Blocks.Count(v => v != TerrainBlocks.Air),
                "Non-air section count out of sync");
            Check(section.FluidCount == section.Blocks.Count(v =>
                v is TerrainBlocks.Water or TerrainBlocks.Lava),
                "Fluid section count out of sync");
            oreBlocks += section.Blocks.Count(v => v is
                TerrainBlocks.CoalOre or TerrainBlocks.DeepslateCoalOre or
                TerrainBlocks.IronOre or TerrainBlocks.DeepslateIronOre or
                TerrainBlocks.CopperOre or TerrainBlocks.DeepslateCopperOre or
                TerrainBlocks.GoldOre or TerrainBlocks.DeepslateGoldOre or
                TerrainBlocks.DiamondOre or TerrainBlocks.DeepslateDiamondOre);
            plantBlocks += section.Blocks.Count(v => v is
                TerrainBlocks.ShortGrass or TerrainBlocks.Fern or
                TerrainBlocks.Dandelion or TerrainBlocks.Poppy);
            leafBlocks += section.Blocks.Count(v => v is
                TerrainBlocks.OakLeaves or TerrainBlocks.SpruceLeaves or
                TerrainBlocks.BirchLeaves or TerrainBlocks.DarkOakLeaves);
        }
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int top = ChunkColumn.MinY, solid = ChunkColumn.MinY;
            for (int y = ChunkColumn.MinY; y <= ChunkColumn.MaxY; y++)
            {
                ushort block = column.GetBlock(x, y, z);
                if (block == TerrainBlocks.Air) continue;
                top = y;
                if (block is not (TerrainBlocks.Water or TerrainBlocks.Lava)) solid = y;
            }
            int idx = x + z * 16;
            Check(column.TopY[idx] == top && column.GroundY[idx] == solid,
                "Feature heightmaps are stale");
        }
    }
    Check(oreBlocks > 0, "No seeded ore veins appeared");
    Check(leafBlocks + plantBlocks > 0, "No vegetation appeared");
    Console.WriteLine($"PASS: order-independent chunks; {oreBlocks} ore blocks, " +
        $"{leafBlocks} leaves and {plantBlocks} plants across two columns");

    // Density and material behavior must be backward compatible with v4
    // when all v5 features are disabled and persistence is off.
    var disabled = new ServerOptions
    {
        GeneratorVersion = 5, Seed = baseline.Seed, FeatureDensity = 0,
        OreFrequency = 0, SaveGeneratedChunks = false
    };
    var old = new TerrainWorld(new ServerOptions { GeneratorVersion = 4, Seed = baseline.Seed });
    var noFeatures = new TerrainWorld(disabled);
    ChunkPos oldPos = new(-3, 4);
    Check(old.SerializeSections(oldPos).SequenceEqual(noFeatures.SerializeSections(oldPos)),
        "Version 5 without features changed version-4 terrain");
    Console.WriteLine("PASS: version-4 terrain remains unchanged");

    string savePath = Path.Combine(directory, "persistent");
    var persistent = new ServerOptions
    {
        GeneratorVersion = 5, Seed = baseline.Seed, Biome = "minecraft:forest",
        FeatureDensity = 1.5, OreFrequency = 1.25, SaveGeneratedChunks = true,
        WorldPath = savePath, ViewDistance = 2
    };
    ChunkPos savedPos = new(-5, 2);
    var one = new TerrainWorld(persistent);
    ChunkColumn generated = one.GetColumn(savedPos);
    byte[] expected = one.SerializeSections(savedPos);
    await one.DisposeAsync(); // wait for the bounded background save queue
    Check(File.Exists(Path.Combine(savePath, "world.yaml")), "Missing world YAML manifest");
    string[] files = Directory.GetFiles(Path.Combine(savePath, "chunks"), "*.mc26");
    Check(files.Length == 1, "Generated chunk was not saved once");
    var two = new TerrainWorld(persistent);
    ChunkColumn reloaded = two.GetColumn(savedPos);
    Check(expected.SequenceEqual(two.SerializeSections(savedPos)),
        "Persisted chunk bytes differ after restart");
    Check(generated.TopY.SequenceEqual(reloaded.TopY), "Persisted heightmap differs");
    await two.DisposeAsync();
    Console.WriteLine("PASS: compressed chunks reload identically after restart");

    try
    {
        _ = new TerrainWorld(new ServerOptions
        {
            GeneratorVersion = 5, Seed = persistent.Seed + 1,
            Biome = "minecraft:forest", FeatureDensity = 1.5, OreFrequency = 1.25,
            SaveGeneratedChunks = true, WorldPath = savePath
        });
        throw new Exception("A different seed was allowed to reuse the same saved world");
    }
    catch (InvalidDataException) { }
    byte[] goodChunk = File.ReadAllBytes(files[0]);
    try
    {
        File.WriteAllBytes(files[0], new byte[] { 1, 2, 3, 4 });
        try
        {
            _ = new TerrainWorld(persistent).GetColumn(savedPos);
            throw new Exception("Corrupt chunk was silently regenerated");
        }
        catch (InvalidDataException) { }
    }
    finally { File.WriteAllBytes(files[0], goodChunk); }
    Console.WriteLine("PASS: incompatible world settings and corrupt chunks fail safely");

    // Bedrock/spawn remains protected even after decoration.
    var spawn = new TerrainWorld(new ServerOptions
    {
        GeneratorVersion = 5, FeatureDensity = 2, OreFrequency = 2,
        SaveGeneratedChunks = false
    });
    for (int z = -2; z <= 2; z++)
    for (int x = -2; x <= 2; x++)
    {
        Check(spawn.GetBlock(x, 79, z) == TerrainBlocks.Stone, "Spawn pad overwritten");
        Check(spawn.GetBlock(x, 80, z) == TerrainBlocks.Air, "Spawn obstructed");
    }
    Console.WriteLine("PASS: preserved 5x5 stone spawn platform");
    Console.WriteLine("All Phase 5 tests passed.");
}
finally
{
    Directory.Delete(directory, recursive: true);
}
