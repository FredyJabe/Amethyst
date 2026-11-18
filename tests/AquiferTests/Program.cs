using Amethyst;
using Amethyst.Generation.Terrain;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
}

var v7Settings = new ServerOptions
{
    GeneratorVersion = 7,
    Seed = 87420913,
    SeaLevel = 63,
    AquifersEnabled = true,
    SaveGeneratedChunks = false
};
var oldSettings = new ServerOptions
{
    GeneratorVersion = 6,
    Seed = v7Settings.Seed,
    SeaLevel = v7Settings.SeaLevel,
    SaveGeneratedChunks = false
};
var v7 = new AquiferSampler(v7Settings);
var again = new AquiferSampler(v7Settings);
var legacy = new AquiferSampler(oldSettings);
int dryCount = 0, waterCount = 0, lavaCount = 0, legacyWaterCount = 0;
bool changed = false;
for (int x = -640; x <= 640; x += 53)
for (int z = -640; z <= 640; z += 59)
{
    ushort state = v7.CaveFluid(x, 0, z, 135);
    Check(state == again.CaveFluid(x, 0, z, 135),
        $"Aquifer deterministic at {x},{z}");
    if (state == TerrainBlocks.Water) waterCount++;
    else if (state == TerrainBlocks.Air) dryCount++;
    else throw new Exception("Unexpected fluid at Y=0");
    if (legacy.CaveFluid(x, 0, z, 135) == TerrainBlocks.Water) legacyWaterCount++;
    if (legacy.CaveFluid(x, 0, z, 135) != state) changed = true;
    if (v7.CaveFluid(x, -58, z, 135) == TerrainBlocks.Lava) lavaCount++;
    Check(v7.CaveFluid(x, 133, z, 135) == TerrainBlocks.Air,
        "Near-surface caves dry");
}
Check(changed, "v7 aquifer mapping differs from v6");
Console.WriteLine($"PASS: v7 sample water={waterCount}, dry={dryCount}, deepLava={lavaCount}, legacyWater={legacyWaterCount}");
Check(dryCount > waterCount && waterCount > 0,
    $"Most v7 underground caves dry (dry={dryCount} water={waterCount})");
Check(legacyWaterCount > waterCount,
    $"v7 flooding reduced (v6={legacyWaterCount}, v7={waterCount})");
Check(lavaCount > 0, "Some deep cavities contain lava");

// Do not save world files in the test working directory.
var w7 = new TerrainWorld(v7Settings);
var w6 = new TerrainWorld(oldSettings);
Check(w7.UsesAquifers && w6.UsesAquifers, "Aquifer feature remains enabled");
Check(w7.SampleTerrain(123, -155) == w6.SampleTerrain(123, -155),
    "v7 preserves v6 landscape geometry");
Check(w7.GetColumn(new ChunkPos(0, 0)).GetBlock(0, TerrainWorld.PlatformY, 0)
    == TerrainBlocks.Stone, "Spawn platform remains intact");
Console.WriteLine("AquiferTests: PASS");
