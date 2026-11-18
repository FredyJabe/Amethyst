using Amethyst;
using Amethyst.Generation.Terrain;
using Amethyst.Generation.Noise;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

var referenceNoise = new FractalNoise3D(87420913, 0x3333444455556666UL, 3);
Check(Math.Abs(referenceNoise.Sample(0, 0, 0, 74) - (-0.21558747143703041)) < 1e-10,
    "Independent 3D noise reference value at origin has changed");
Check(Math.Abs(referenceNoise.Sample(31, -17, -129, 74) - (-0.29959499647230819)) < 1e-10,
    "Independent 3D noise reference value at negative coordinate has changed");
Console.WriteLine("PASS: independent seeded noise reference vectors");

var settings = new ServerOptions
{
    GeneratorVersion = 3, Seed = 87420913, ViewDistance = 2,
    ClimateScale = 1, TerrainScale = 1, BaseHeight = 70,
    SeaLevel = 63, HeightVariation = 36, Biome = "auto"
};
var terrain = new TerrainGenerator(settings);
var sampler = new DensitySampler(terrain, settings);
var repeat = new DensitySampler(new TerrainGenerator(settings), settings);
var changed = new DensitySampler(new TerrainGenerator(new ServerOptions
{
    GeneratorVersion = 3, Seed = settings.Seed + 1
}), new ServerOptions { GeneratorVersion = 3, Seed = settings.Seed + 1 });

Check(terrain.GeneratorVersion == 3, "Expected the version 3 terrain path");
// Boundary points are sampled in WORLD coordinates, including negative chunks.
foreach (var pos in new[] { new ChunkPos(-3, 5), new ChunkPos(-1, -1),
                            new ChunkPos(0, 0), new ChunkPos(9, -7) })
{
    DensityGrid grid = sampler.BuildGrid(pos);
    DensityGrid identical = repeat.BuildGrid(pos);
    DensityGrid east = sampler.BuildGrid(new ChunkPos(pos.X + 1, pos.Z));
    DensityGrid south = sampler.BuildGrid(new ChunkPos(pos.X, pos.Z + 1));
    for (int gy = 0; gy < DensityGrid.GridHeight; gy++)
    for (int i = 0; i < DensityGrid.GridWidth; i++)
    {
        Check(grid.Corner(4, gy, i) == east.Corner(0, gy, i),
            $"East density seam at {pos}, Y cell {gy}");
        Check(grid.Corner(i, gy, 4) == south.Corner(i, gy, 0),
            $"South density seam at {pos}, Y cell {gy}");
        Check(grid.Corner(0, gy, i) == identical.Corner(0, gy, i),
            "Same seed must give identical corners");
    }
    Check(grid.Interpolate(16, 79, 3) == east.Interpolate(0, 79, 3),
        "Interpolated density must be continuous across chunk edge");
}
Check(Math.Abs(sampler.Sample(300, 12, -500) - changed.Sample(300, 12, -500)) > 0.001,
    "Changing the seed must change the density field");
Console.WriteLine("PASS: seeded 3D density, shared 4x8x4 corners and negative-coordinate seams");

var world = new TerrainWorld(settings);
var legacy = new TerrainWorld(new ServerOptions { GeneratorVersion = 2, Seed = settings.Seed,
    ClimateScale = settings.ClimateScale, TerrainScale = settings.TerrainScale,
    BaseHeight = settings.BaseHeight, SeaLevel = settings.SeaLevel,
    HeightVariation = settings.HeightVariation, ViewDistance = 2 });
Check(world.UsesDensity && !legacy.UsesDensity, "Version 3 must be opt-in to density filling");
foreach (var pos in new[] { (0, 0), (120, -80), (-620, 300) })
    Check(world.SampleTerrain(pos.Item1, pos.Item2) == legacy.SampleTerrain(pos.Item1, pos.Item2),
        "Version 3 must preserve Phase 2 biome/shape inputs");

for (int z = -2; z <= 2; z++)
for (int x = -2; x <= 2; x++)
{
    Check(world.GetBlock(x, 79, z) == TerrainBlocks.Stone, "Spawn pad is missing");
    Check(world.GetBlock(x, 78, z) == TerrainBlocks.Stone, "Spawn pad has no support");
    Check(world.GetBlock(x, 80, z) == TerrainBlocks.Air, "Spawn is obstructed");
}
Console.WriteLine("PASS: versions 1/2 left intact; spawn platform is safe in version 3");

bool foundUndergroundVoid = false;
int sectionsVerified = 0;
// Include negative coordinates and distant terrain, without depending on a
// single specific cave location. This detects a heightmap-only regression.
foreach (var pos in new[] { new ChunkPos(6, 6), new ChunkPos(-9, 4),
                             new ChunkPos(13, -11), new ChunkPos(-22, -14),
                             new ChunkPos(28, 22), new ChunkPos(-31, 18),
                             new ChunkPos(3, -17), new ChunkPos(19, 7) })
{
    ChunkColumn column = world.GetColumn(pos);
    for (int z = 0; z < 16; z++)
    for (int x = 0; x < 16; x++)
    {
        int index = x + z * 16;
        int highestSolid = ChunkColumn.MinY;
        int highestBlock = ChunkColumn.MinY;
        for (int y = ChunkColumn.MinY; y <= ChunkColumn.MaxY; y++)
        {
            ushort block = column.GetBlock(x, y, z);
            if (block != TerrainBlocks.Air)
                highestBlock = y;
            if (block != TerrainBlocks.Air && block != TerrainBlocks.Water)
                highestSolid = y;
            if (y >= ChunkColumn.MinY + 24 && y < column.GroundY[index] - 25 &&
                block == TerrainBlocks.Air &&
                column.GetBlock(x, y - 16, z) != TerrainBlocks.Air)
                foundUndergroundVoid = true;
        }
        Check(column.GroundY[index] == highestSolid,
            "GroundY must describe highest solid block, not 2D shaped elevation");
        Check(column.TopY[index] == highestBlock,
            "Heightmap top must include ocean water and overhangs");
        Check(column.GetBlock(x, ChunkColumn.MinY, z) == TerrainBlocks.Bedrock,
            "World floor must remain bedrock");
    }
    foreach (WorldSection section in column.Sections)
    {
        Check(section.NonAir == section.Blocks.Count(b => b != TerrainBlocks.Air),
            "Non-air section count does not match generated blocks");
        Check(section.FluidCount == section.Blocks.Count(b => b == TerrainBlocks.Water),
            "Fluid section count does not match generated water states");
        Check(section.Biomes.Length == 64, "Expected 4x4x4 biome samples");
        sectionsVerified++;
    }
}
Check(foundUndergroundVoid, "Expected at least one dry subterranean cavity in sampled chunks");
Console.WriteLine($"PASS: 3D air cavities, correct heightmaps, bedrock, 24-section counts ({sectionsVerified} sections)");
Console.WriteLine("All Phase 3 density checks passed.");
