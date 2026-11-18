using Amethyst;
using Amethyst.Generation.Climate;
using Amethyst.Generation.Noise;

static void Expect(bool ok, string message)
{
    if (!ok) throw new InvalidDataException(message);
}

long seed = 87420913;
var sample = new ClimateSampler(seed);
var again = new ClimateSampler(seed);
var different = new ClimateSampler(seed + 1);
var zoomed = new ClimateSampler(seed, 2);

// In particular, world origin must be different between seeds: gradient noise
// without deterministic per-octave offsets has a zero-at-origin bug.
Expect(sample.Sample(0, 0) != different.Sample(0, 0),
    "Seed must affect climate at the world origin");
Expect(sample.Sample(0, 0) == again.Sample(0, 0),
    "Seeded origin must repeat across generator instances");
// Independently calculated Phase-1 golden values. Changes to these values
// require a deliberate generator version bump (not an accidental refactor).
ClimateSample origin = sample.Sample(0, 0);
Expect(Math.Abs(origin.Temperature - 0.15594699213105837) < 1e-9 &&
       Math.Abs(origin.Humidity - (-0.40827021892914966)) < 1e-9 &&
       Math.Abs(origin.Continentalness - 0.446199997787051) < 1e-9 &&
       Math.Abs(origin.Erosion - 0.765185908789123) < 1e-9 &&
       Math.Abs(origin.Weirdness - 0.32393915999577905) < 1e-9,
    "Phase 1 golden climate values changed without generator version migration");

var points = new (int X, int Z)[]
{
    (0, 0), (1, 1), (15, 15), (16, 16), (-1, -1), (-16, -16),
    (-17, 19), (2048, -2048), (2500000, -2500000), (500000, 42)
};
foreach (var (x, z) in points)
{
    ClimateSample a = sample.Sample(x, z);
    Expect(a == again.Sample(x, z), $"Different result at ({x}, {z})");
    foreach (double v in new[] { a.Temperature, a.Humidity, a.Continentalness,
                                 a.Erosion, a.Weirdness, a.PeaksAndValleys })
        Expect(double.IsFinite(v) && v is >= -1 and <= 1,
            $"Climate value out of bounds at ({x}, {z}): {v}");
}
Expect(points.Any(p => sample.Sample(p.X, p.Z) != different.Sample(p.X, p.Z)),
    "Other seeds must produce different climate regions");
Expect(points.Any(p => sample.Sample(p.X, p.Z) != zoomed.Sample(p.X, p.Z)),
    "Changing climateScale must alter climate field size");
Console.WriteLine("PASS: five finite climate fields, deterministic seeds, scale, negative coordinates");

// Climate is sampled in absolute coordinates, without local-chunk resets.
// Adjacent positions across positive AND negative chunk edges should be smooth.
foreach (var (x, z) in new[] { (15, 52), (-17, 52), (128, -17), (128, 15) })
{
    ClimateSample a = sample.Sample(x, z), b = sample.Sample(x + 1, z);
    Expect(Math.Abs(a.Temperature - b.Temperature) < 0.15 &&
           Math.Abs(a.Humidity - b.Humidity) < 0.15 &&
           Math.Abs(a.Continentalness - b.Continentalness) < 0.15,
        $"Seam or abrupt transition at ({x}, {z})");
}
Console.WriteLine("PASS: smooth climate sampling across chunk boundaries");

// A 3D gradient primitive is provided for upcoming density/cave phases.
var noise = new SeededGradientNoise(seed, 0xCAFEBABEUL);
Expect(noise.Sample2D(1.125, -2.25) == noise.Sample2D(1.125, -2.25),
    "2D noise not deterministic");
Expect(noise.Sample3D(1.125, 3.25, -2.25) != noise.Sample3D(1.125, 3.75, -2.25),
    "3D noise is not varying with Y");
var f = new FractalNoise2D(seed, 0x44UL);
Expect(f.Sample(0, 0, 900) != new FractalNoise2D(seed + 1, 0x44UL).Sample(0, 0, 900),
    "Fractal noise must depend on seed at origin");
Console.WriteLine("PASS: reusable 2D/3D seeded gradient and octave noise");

var resolver = new BiomeResolver();
BiomeSelection Ocean(ClimateSample c, int height) => resolver.Resolve(c, height, 63, 70, 36);
Expect(Ocean(new ClimateSample(0, 0, -0.7, 0.1, 0), 40).Name == "minecraft:deep_ocean",
    "Deep underwater column must select deep ocean");
Expect(Ocean(new ClimateSample(0.8, -0.25, 0.2, 0, 0), 72).Name == "minecraft:desert",
    "Hot/dry lowlands should select desert");
Expect(Ocean(new ClimateSample(-0.9, -0.4, 0, 0, 0), 72).Name == "minecraft:snowy_plains",
    "Cold/dry lowlands should select snowy plains");
Expect(Ocean(new ClimateSample(0, 0, 0, 0, 0), 79).BiomeId >= 0,
    "Biome resolver should return a valid registry ID");
Expect(resolver.Resolve(new ClimateSample(0.9, -0.9, 0, 0, 0), 100, 63, 70, 36, true).Name
    == "minecraft:plains", "Spawn protection must preserve plains");
Expect(new BiomeResolver("minecraft:cherry_grove")
    .Resolve(new ClimateSample(0.8, -0.9, 0, 0, 0), 90, 63, 70, 36, true).Name
    == "minecraft:cherry_grove", "Forced biome should override climate and spawn rules");
// Exercise all five climate fields in the same world geometry.
Expect(Ocean(new ClimateSample(0, 0.5, 0, 0, 0), 74).Name !=
       Ocean(new ClimateSample(0, -0.5, 0, 0, 0), 74).Name,
    "Humidity must affect the biome resolver");
Expect(Ocean(new ClimateSample(0.8, 0, -0.5, 0, 0), 56).Name == "minecraft:warm_ocean" &&
       Ocean(new ClimateSample(0.8, 0, 0.5, 0, 0), 56).Name == "minecraft:ocean",
    "Continentalness must affect warm ocean classification");
Expect(Ocean(new ClimateSample(0, 0, 0, -0.7, 0), 110).Name !=
       Ocean(new ClimateSample(0, 0, 0, 0.7, 0), 110).Name,
    "Erosion should influence highland biome classification");
Expect(Ocean(new ClimateSample(0, 0, 0, 0, 0.5), 80).Name == "minecraft:meadow" &&
       Ocean(new ClimateSample(0, 0, 0, 0, 0), 80).Name == "minecraft:plains",
    "Folded weirdness must influence valley/meadow classification");
Console.WriteLine("PASS: all five fields, sea level, temperature, spawn and forced-biome rules");

var settings = new ServerOptions { Seed = seed, GeneratorVersion = 1, ClimateScale = 1.0 };
var terrain = new TerrainWorld(settings);
var scaled = new TerrainWorld(new ServerOptions { Seed = seed, GeneratorVersion = 1, ClimateScale = 2.0 });
foreach (var (x, z) in points)
{
    Expect(terrain.SampleClimate(x, z) == sample.Sample(x, z),
        "World climate must use the standalone ClimateSampler");
    Expect(terrain.GetGroundHeight(x, z) == scaled.GetGroundHeight(x, z),
        "Climate scaling must not change Phase 1 terrain geometry");
    int id = terrain.SampleTerrain(x, z).BiomeId;
    Expect(id >= 0, "World biome must have a valid registry ID");
}
Expect(terrain.GetBlock(0, 79, 0) == TerrainBlocks.Stone,
    "5x5 stone platform must remain after climate integration");
Console.WriteLine("PASS: integrated world biome sampling, unchanged height geometry and spawn pad");

string yamlPath = Path.Combine(Path.GetTempPath(), $"minicraft-climate-{Guid.NewGuid():N}.yaml");
try
{
    File.WriteAllText(yamlPath, "seed: -42\ngeneratorVersion: 1\nclimateScale: 2.5\nbiome: auto\n");
    ServerOptions fromYaml = ServerOptions.Load(yamlPath);
    Expect(fromYaml.Seed == -42 && fromYaml.GeneratorVersion == 1 &&
           fromYaml.ClimateScale == 2.5, "YAML climate options not loaded");
    File.WriteAllText(yamlPath, "generatorVersion: 99\n");
    bool rejected = false;
    try { ServerOptions.Load(yamlPath); }
    catch (NotSupportedException) { rejected = true; }
    Expect(rejected, "Unsupported generator version must fail fast");
}
finally { File.Delete(yamlPath); }
Console.WriteLine("PASS: YAML climate settings and incompatible generator version rejection");
Console.WriteLine("ALL CLIMATE TESTS PASSED");
