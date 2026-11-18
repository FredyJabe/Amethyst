using Amethyst;
using Amethyst.Generation.Climate;
using Amethyst.Generation.Terrain;

static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

var settings = new ServerOptions
{
    Seed = 87420913,
    GeneratorVersion = 2,
    BaseHeight = 70,
    SeaLevel = 63,
    HeightVariation = 36,
    ClimateScale = 1.0,
    TerrainScale = 1.0
};
var shaper = new TerrainShaper(settings);
var shaperAgain = new TerrainShaper(settings);

// Controlled climate inputs isolate continental shape from the noise field.
const int x = 4096, z = -8192;
var deep = new ClimateSample(0, 0, -0.75, 0.7, 0);
var coast = new ClimateSample(0, 0, -0.15, 0.7, 0);
var inland = new ClimateSample(0, 0, 0.35, 0.7, 0.25);
double oceanY = shaper.Shape(x, z, deep).Height;
double coastY = shaper.Shape(x, z, coast).Height;
double landY = shaper.Shape(x, z, inland).Height;
Expect(oceanY < settings.SeaLevel - 15 && coastY < settings.SeaLevel + 4 &&
       landY > settings.SeaLevel + 10,
    $"Continent elevations unexpected: {oceanY}, {coastY}, {landY}");
Expect(oceanY < coastY && coastY < landY,
    "Continentalness must continuously raise deep ocean -> coast -> inland");

// No mountain chains with high erosion, but lower erosion allows folded
// weirdness to elevate continuous interior mountain ranges.
var smooth = new ClimateSample(0, 0, 0.75, 0.85, 0.5);
var rugged = smooth with { Erosion = -0.75 };
var valley = rugged with { Weirdness = 0.0 };
TerrainShape smoothResult = shaper.Shape(x, z, smooth);
TerrainShape mountainResult = shaper.Shape(x, z, rugged);
TerrainShape valleyResult = shaper.Shape(x, z, valley);
Expect(mountainResult.MountainRelief > smoothResult.MountainRelief + 20,
    "Lower erosion should permit substantially higher mountain relief");
Expect(mountainResult.Height > smoothResult.Height + 15,
    "Mountain relief should affect the actual terrain height");
Expect(mountainResult.Height > valleyResult.Height + 15,
    "Folded weirdness should distinguish peaks from valleys");
Console.WriteLine("PASS: continental splines, erosion, mountains and valleys");

// River contours belong to a separate deterministic domain; with lowland
// climate at least part of a long transect should cut a valley.
var lowland = new ClimateSample(0, 0, 0.1, 0.6, 0);
TerrainShape[] riverTransect = Enumerable.Range(-256, 513)
    .Select(i => shaper.Shape(i * 16, 1536, lowland)).ToArray();
Expect(riverTransect.Any(t => t.RiverInfluence > 0.65),
    "Expected a river valley along the controlled lowland transect");
Expect(riverTransect.Any(t => t.RiverInfluence < 0.01),
    "River effect should be localized rather than filling the landscape");
var riverBiome = new BiomeResolver().Resolve(lowland, 60, 63, 70, 36, river: true);
Expect(riverBiome.Name == "minecraft:river" && riverBiome.Surface == SurfaceKind.Riverbed,
    "Wet riverbeds must select the 26.3 river biome and gravel surface");
var frozenRiver = new BiomeResolver().Resolve(lowland with { Temperature = -0.9 },
    60, 63, 70, 36, river: true);
Expect(frozenRiver.Name == "minecraft:frozen_river", "Cold riverbeds should select frozen rivers");
Console.WriteLine("PASS: meandering drainage valleys and river biome rules");

// Repeatability, negative coordinates, world-space continuity, seed domains,
// independent terrain/climate scales, and backward-compatible generator v1.
var a = new TerrainGenerator(settings);
var b = new TerrainGenerator(settings);
var changedSeed = new TerrainGenerator(new ServerOptions { Seed = settings.Seed + 1, GeneratorVersion = 2 });
var changedClimate = new TerrainGenerator(new ServerOptions { Seed = settings.Seed, GeneratorVersion = 2, ClimateScale = 2 });
var changedRelief = new TerrainGenerator(new ServerOptions { Seed = settings.Seed, GeneratorVersion = 2, TerrainScale = 2 });
var legacy = new TerrainGenerator(new ServerOptions { Seed = settings.Seed, GeneratorVersion = 1 });
var coords = new[] { (0, 0), (15, 15), (16, 15), (-1, -1), (-16, -17),
                     (2048, -2048), (7777, -9999), (-40000, 10000) };
foreach (var (px, pz) in coords)
{
    Expect(a.Sample(px, pz) == b.Sample(px, pz),
        $"Shape differs across instances at ({px}, {pz})");
    Expect(shaper.Shape(px, pz, a.SampleClimate(px, pz)) ==
           shaperAgain.Shape(px, pz, a.SampleClimate(px, pz)),
        "Shape must be deterministic, independent of generation order");
    Expect(a.Sample(px, pz).GroundY is > ChunkColumn.MinY and < ChunkColumn.MaxY,
        "Phase-2 elevation must fit Overworld vertical range");
}
Expect(coords.Any(p => a.Sample(p.Item1, p.Item2) != changedSeed.Sample(p.Item1, p.Item2)),
    "Changing seed must change generated terrain/biomes");
Expect(coords.Any(p => a.SampleShape(p.Item1, p.Item2).Height !=
                       changedClimate.SampleShape(p.Item1, p.Item2).Height),
    "Version 2 must use climate scale in the terrain elevation");
Expect(coords.Any(p => a.SampleShape(p.Item1, p.Item2).Height !=
                       changedRelief.SampleShape(p.Item1, p.Item2).Height),
    "Changing terrainScale must change v2 local relief");
Expect(coords.Any(p => a.Sample(p.Item1, p.Item2).GroundY !=
                       legacy.Sample(p.Item1, p.Item2).GroundY),
    "Generator version 2 should not silently reuse v1 heights");
Expect(a.Sample(0, 0).GroundY == 79 && legacy.Sample(0, 0).GroundY == 79,
    "The 5x5 spawn approach must remain at Y=79 in both versions");
foreach (var (px, pz) in new[] { (15, -48), (-17, 47), (-1, -17), (255, 256) })
{
    int dy = Math.Abs(a.Sample(px, pz).GroundY - a.Sample(px + 1, pz).GroundY);
    Expect(dy <= 3, $"Abrupt chunk seam at ({px},{pz}): delta {dy}");
}
Console.WriteLine("PASS: deterministic seeds, coordinate continuity, independent scales, v1 compatibility");

var world = new TerrainWorld(settings);
for (int dx = -2; dx <= 2; dx++)
for (int dz = -2; dz <= 2; dz++)
{
    Expect(world.GetBlock(dx, 79, dz) == TerrainBlocks.Stone,
        "The 5x5 spawn platform must remain stone");
    Expect(world.GetBlock(dx, 80, dz) == TerrainBlocks.Air,
        "Players must have unobstructed headroom at spawn");
}
Expect(world.GetBlock(-1, 78, -1) != TerrainBlocks.Air,
    "Spawn platform must be supported by terrain");
Console.WriteLine("PASS: spawn platform and cuboid-world integration");

string yamlPath = Path.Combine(Path.GetTempPath(), $"minicraft-phase2-{Guid.NewGuid():N}.yaml");
try
{
    File.WriteAllText(yamlPath, "seed: -42\ngeneratorVersion: 2\nclimateScale: 1.2\nterrainScale: 0.8\n");
    ServerOptions yaml = ServerOptions.Load(yamlPath);
    Expect(yaml.Seed == -42 && yaml.GeneratorVersion == 2 && yaml.ClimateScale == 1.2 &&
           yaml.TerrainScale == 0.8, "YAML v2 terrain settings not parsed correctly");
    File.WriteAllText(yamlPath, "generatorVersion: 1\n");
    Expect(ServerOptions.Load(yamlPath).GeneratorVersion == 1,
        "Existing v1 world settings must remain supported");
    File.WriteAllText(yamlPath, "generatorVersion: 99\n");
    bool invalidRejected = false;
    try { ServerOptions.Load(yamlPath); }
    catch (NotSupportedException) { invalidRejected = true; }
    Expect(invalidRejected, "Unknown generator versions must be rejected");
}
finally { File.Delete(yamlPath); }
Console.WriteLine("PASS: YAML generator-version configuration and migration safety");
Console.WriteLine("ALL PHASE-2 TERRAIN SHAPING TESTS PASSED");
