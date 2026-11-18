using Amethyst;
using Amethyst.Generation.Climate;
using Amethyst.Generation.Terrain;

static void Check(bool ok, string reason)
{
    if (!ok) throw new Exception(reason);
}

var settings = new ServerOptions
{
    Seed = 87420913,
    GeneratorVersion = 4,
    AquifersEnabled = true,
    SeaLevel = 63,
    AquiferLavaLevel = -48,
    AquiferWaterTableOffset = 18,
    SurfaceDepth = 4,
    ViewDistance = 2
};

// The YAML path should activate v4 and validate its optional parameters.
string yamlPath = Path.Combine(Path.GetTempPath(),
    "minicraft-phase4-" + Guid.NewGuid().ToString("N") + ".yaml");
try
{
    File.WriteAllText(yamlPath,
        "seed: -98412\ngeneratorVersion: 4\naquifersEnabled: false\n" +
        "aquiferLavaLevel: -50\naquiferWaterTableOffset: 21\nsurfaceDepth: 6\n");
    ServerOptions fromYaml = ServerOptions.Load(yamlPath);
    Check(fromYaml.Seed == -98412 && fromYaml.GeneratorVersion == 4 &&
          !fromYaml.AquifersEnabled && fromYaml.AquiferLavaLevel == -50 &&
          fromYaml.AquiferWaterTableOffset == 21 && fromYaml.SurfaceDepth == 6,
        "Phase-4 YAML options failed to load");
    File.WriteAllText(yamlPath, "generatorVersion: 4\nsurfaceDepth: 20\n");
    try
    {
        ServerOptions.Load(yamlPath);
        throw new Exception("Invalid surfaceDepth was accepted");
    }
    catch (ArgumentOutOfRangeException) { }
}
finally { File.Delete(yamlPath); }
Console.WriteLine("PASS: Phase-4 YAML configuration and validation");

var water = new AquiferSampler(settings);
var waterAgain = new AquiferSampler(settings);
var waterOther = new AquiferSampler(new ServerOptions { Seed = settings.Seed + 1 });
bool waterChangesWithSeed = false, foundWater = false, foundLava = false;
for (int z = -150; z <= 150; z += 21)
for (int x = -150; x <= 150; x += 19)
{
    Check(water.WaterTable(x, z) == waterAgain.WaterTable(x, z),
        "Water table must be deterministic");
    Check(water.LavaTable(x, z) == waterAgain.LavaTable(x, z),
        "Lava table must be deterministic");
    if (water.WaterTable(x, z) != waterOther.WaterTable(x, z))
        waterChangesWithSeed = true;
    foundWater |= water.CaveFluid(x, -12, z, 150) == TerrainBlocks.Water;
    foundLava |= water.CaveFluid(x, -58, z, 150) == TerrainBlocks.Lava;
    Check(water.CaveFluid(x, 146, z, 150) == TerrainBlocks.Air,
        "Exposed/open-surface caves must stay dry");
    Check(Math.Abs(water.WaterTable(x, z) - water.WaterTable(x + 1, z)) <= 3,
        "Aquifer water levels must not step sharply across blocks/chunk seams");
}
Check(waterChangesWithSeed, "Water table ignores world seed");
Check(foundWater && foundLava, "Aquifer sampler did not produce both water and lava");
Console.WriteLine("PASS: independently seeded aquifer levels and deterministic cave fluids");

var rules = new MaterialRuleEngine(settings);
Check(rules.BaseRock(-18, -20, 30) == TerrainBlocks.Deepslate,
    "Deep solid terrain must become deepslate");
Check(rules.BaseRock(-18, 20, 30) == TerrainBlocks.Stone,
    "High solid terrain must remain stone");
Check(MaterialRuleEngine.SelectSurface(SurfaceKind.Grass,
    "minecraft:badlands", 0, TerrainBlocks.Stone) == TerrainBlocks.RedSand,
    "Badlands should use red sand on the exposed surface");
Check(MaterialRuleEngine.SelectSurface(SurfaceKind.Grass,
    "minecraft:badlands", 2, TerrainBlocks.Stone) == TerrainBlocks.Terracotta,
    "Badlands should have terracotta under the top layer");
Check(MaterialRuleEngine.SelectSurface(SurfaceKind.Grass,
    "minecraft:swamp", 0, TerrainBlocks.Stone) == TerrainBlocks.Mud,
    "Swamps should have a muddy surface");
Check(MaterialRuleEngine.SelectSurface(SurfaceKind.Sand,
    "minecraft:desert", 3, TerrainBlocks.Stone) == TerrainBlocks.Sandstone,
    "Deserts should transition from sand to sandstone");
Check(MaterialRuleEngine.IsFluid(TerrainBlocks.Lava) &&
      MaterialRuleEngine.IsFluid(TerrainBlocks.Water) &&
      !MaterialRuleEngine.IsFluid(TerrainBlocks.Deepslate),
    "Correct fluid-state classification");
Console.WriteLine("PASS: 26.3 material-rule state IDs, biome rules and rock transitions");

var caves = new CaveBiomeSampler(settings.Seed);
int plains = VanillaRegistries.IndexOf("minecraft:worldgen/biome", "minecraft:plains");
Check(caves.Resolve(14, 90, -23, 80, plains) == plains,
    "Surface biome changed when sampling a shallow/above-ground Y");
int[] knownCaves =
[
    VanillaRegistries.IndexOf("minecraft:worldgen/biome", "minecraft:lush_caves"),
    VanillaRegistries.IndexOf("minecraft:worldgen/biome", "minecraft:dripstone_caves"),
    VanillaRegistries.IndexOf("minecraft:worldgen/biome", "minecraft:deep_dark")
];
foreach (int x in new[] { -60, 0, 63 })
{
    int id = caves.Resolve(x, -40, -200, 100, plains);
    Check(id == plains || knownCaves.Contains(id), "Invalid cave-biome ID");
}
Console.WriteLine("PASS: cave biome registry IDs and depth gating");

var v4 = new TerrainWorld(settings);
var v4Again = new TerrainWorld(settings);
var old = new TerrainWorld(new ServerOptions { GeneratorVersion = 3, Seed = settings.Seed });
var dryV4 = new TerrainWorld(new ServerOptions
    { GeneratorVersion = 4, Seed = settings.Seed, AquifersEnabled = false });
Check(v4.UsesDensity && v4.UsesAquifers && !old.UsesAquifers && !dryV4.UsesAquifers,
    "Phase 4 version gating or aquifer disabling is broken");
foreach (var point in new[] { (0, 0), (256, -512), (-800, -500) })
    Check(v4.SampleTerrain(point.Item1, point.Item2) == old.SampleTerrain(point.Item1, point.Item2),
        "Version 4 must retain Version 3 climate and base terrain shape");

foreach (var pos in new[] { new ChunkPos(-3, 4), new ChunkPos(4, -3) })
{
    ChunkColumn a = v4.GetColumn(pos), b = v4Again.GetColumn(pos);
    for (int section = 0; section < ChunkColumn.SectionCount; section++)
    {
        WorldSection sa = a.Sections[section], sb = b.Sections[section];
        Check(sa.Blocks.SequenceEqual(sb.Blocks), "Same seed produced different cave materials");
        Check(sa.Biomes.SequenceEqual(sb.Biomes), "Same seed produced different cave biomes");
        Check(sa.NonAir == sa.Blocks.Count(block => block != TerrainBlocks.Air),
            "Incorrect non-air section count");
        Check(sa.FluidCount == sa.Blocks.Count(MaterialRuleEngine.IsFluid),
            "Incorrect water + lava section fluid count");
        Check(sa.Biomes.Length == 64, "Biome array must be 4x4x4");
    }
    for (int z = 0; z < 16; z++)
    for (int x = 0; x < 16; x++)
    {
        int index = x + z * 16;
        int topBlock = ChunkColumn.MinY, topSolid = ChunkColumn.MinY;
        for (int y = ChunkColumn.MinY; y <= ChunkColumn.MaxY; y++)
        {
            ushort block = a.GetBlock(x, y, z);
            if (block == TerrainBlocks.Air) continue;
            topBlock = y;
            if (!MaterialRuleEngine.IsFluid(block)) topSolid = y;
        }
        Check(a.GroundY[index] == topSolid, "Solid heightmap mismatch");
        Check(a.TopY[index] == topBlock, "World surface heightmap mismatch");
    }
    Check(v4.SerializeSections(pos).SequenceEqual(v4Again.SerializeSections(pos)),
        "Chunk packet sections must remain deterministic");
}
for (int z = -2; z <= 2; z++)
for (int x = -2; x <= 2; x++)
{
    Check(v4.GetBlock(x, 79, z) == TerrainBlocks.Stone, "Stone spawn platform missing");
    Check(v4.GetBlock(x, 80, z) == TerrainBlocks.Air, "Spawn blocked");
}
Console.WriteLine("PASS: 3D block generation, biome arrays, valid section metadata and spawn pad");
Console.WriteLine("All Phase 4 tests passed.");
